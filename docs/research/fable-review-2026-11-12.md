# Independent review of TradingFlow: strategies, architecture and a plan (12 November 2026)

Reviewer: Fable, acting as an independent quantitative researcher and trading-systems architect.
Scope: read-only review of `C:\project\trading_flow_swing` (branch `approved-execution-and-signal-study`)
plus published research found by web search on 12 November 2026. Nothing was built, run, downloaded
or changed except this file. No market data, database or broker account was opened.

How to read this document:

- **[Checked today]** means I read the source (a web page or a file in this repository) today.
- **[From memory]** means a well-known result I did not re-open today. Treat it as a pointer, not a proof.
- **[My estimate]** means my own arithmetic or judgement. It is not a measurement.
- Technical words are explained where they first appear.

---

## 0. The ten things I would want the owner to know

1. **The platform is better than the experiments run on it.** The engineering discipline (one decision
   path, frozen inputs, identical re-runs, named skip reasons, honest loss counting) is unusually good.
   The experiments so far were too small to tell anything: 25 hand-picked stocks for one to six months
   cannot separate skill from luck, in either direction.
2. **The "complete-stocks" list removes exactly the trades that hurt a no-stop strategy.** The rule
   drops, for the whole test, every stock that at any time in the window opened 45% or more below its
   previous close, had any split, or missed a single session. A stock that will crash 45% overnight
   next March is therefore never bought this January. That uses information from the future. The
   worst trade and the drawdown of Connors 9.x and of the short are not believable under this rule
   (section 2.1). The fix is already half built in the code.
3. **One year cannot answer the Connors question, and the "fail if the average is at or below zero"
   rule will retire a good strategy about one time in four** (my estimate, section 1.1). You already
   hold ten years of whole-market daily bars. Use them for the question "is there an edge", and keep
   the one hourly year for the question "what does entering in the last hour change".
4. **The freshest year is being spent on an underpowered test.** Your own rule says a period viewed
   for one strategy counts as viewed. Running the one-year Connors test on 3 Nov 2025 - 30 Oct 2026
   uses up the most recent year for every later swing strategy on those stocks. I would finish the
   one-month engineering check and leave the other eleven months unopened for now.
5. **ORB's result is decided by a cost assumption, not by evidence.** With the stop only 0.3-0.6% of
   price away, our 10 basis points of slippage per side (a basis point is one hundredth of one
   percent) costs roughly a third to two thirds of the planned loss on every trade before the market
   moves. The paper's figures use a commission of $0.0035 per share and I could not find that it
   models slippage at all. The overview says "costs no longer explain the result" after looking at
   commission only; the slippage inside the fill prices was not counted (section 1.3).
6. **Connors RSI(2), snapback and "recovery" are one idea, not three**: buy a liquid stock that fell
   hard for no company-specific reason and sell the bounce. The published edge is real but thin:
   about 0.45% per trade before costs on S&P 500 stocks over 1996-2025, with trades bunched on
   sell-off days so a small account cannot take most of them. Expect single-digit yearly returns
   above cash, not more.
7. **Drop the short versions.** No-stop shorts in stocks that just spiked have unlimited loss, the
   evidence is weak, and borrowing cost and availability are not modelled.
8. **The paper run that just started does not shadow any backtest.** It trades a current Finviz S&P
   500 list with 5 positions of 20% and (per the checked-in profile) limit entry orders, while the
   test under way uses the whole market, 10 positions of 10% and last-hour market fills. It is a
   useful operations drill; its profit or loss says nothing about the tested strategy. I also could
   not find the paper authorization in the checked-in catalog, and `docs/operating-boundaries.md`
   still says no strategy is eligible.
9. **Loss limits are declared but I found no code that enforces them.** `max_daily_loss_pct`,
   `max_weekly_loss_pct` and `max_consecutive_losses` exist in the parameter registry; a search of
   `src` finds no reader other than the registry and one configuration test. This must exist before
   real money.
10. **A realistic target** for this account size, run well, is roughly 6-12% a year after costs with
    drawdowns of 12-20%, built from an ETF core plus one or two thin stock edges. There is a real
    chance (I would say 40-50%) that no short-horizon stock rule passes honest tests. The plan in
    section 4 is built so that this outcome is discovered cheaply and early.

---

## 1. Strategy

### 1.0 Why most published edges disappoint

- **Publication decay.** McLean and Pontiff (2016) found that the returns of published stock
  anomalies were 26% lower out of sample and 58% lower after publication **[From memory; the figures
  in the overview are right]**. Chen and Velikov (2023) add costs: the average anomaly nets about
  4 basis points a month after trading costs and data-mining adjustment, and the strongest about
  10 **[Checked today]**. A practical rule: halve any published number for decay, then subtract
  your own costs, and expect less still for strategies that trade often.
- **Luck from many tries.** If you test 200 ideas that all have zero edge, the luckiest one alone
  will show a t-statistic of about 2.7 **[My estimate: the expected largest of 200 random draws]**.
  A t-statistic measures how many standard errors an average is from zero. This project has run
  200+ iterations and retired about 40 strategy versions. So a result with t = 2 means nothing here;
  t of 3 or more is the right bar, as Harvey, Liu and Zhu (2016) argue for the same reason
  **[From memory]**.
- **Who published it.** The Connors book tests 1995-2007 with no out-of-sample check
  **[Checked today, CXO Advisory]**. The ORB paper and the SPY intraday paper come from authors
  connected to a trading firm and a day-trading education business. That does not make them wrong;
  it means nobody without an interest has confirmed them over many years yet.

### 1.1 Connors RSI(2) long (9.x, 7.x)

**What the evidence says.**

- The best independent evidence I found is Concretum's November 2025 study **[Checked today]**:
  every historical S&P 500 member, 1996 to November 2025; buy at the next open when RSI(2) is below 5
  and the close is below its 5-day average; sell at the next open after the first close above the
  5-day average, or after 5 days. Result: 114,189 trades, an average of +0.45% per trade before
  costs, 64% winners. Two things matter for us:
  - The edge survived entering at the next open, on large stocks.
  - Signals are bunched. Half of all trades fall on 3.1% of days, and the most profitable trades come
    on days with about 65 signals at once. A portfolio that can take only 5 (or 10) cannot earn the
    average. The study calls this "opportunity-set bias". This is why your amended rule to also run
    an equal-weight study of every signal is right, and why the portfolio result will be lower than
    the per-signal average.
  - The article gives no cost-adjusted figures and no breakdown by period, so it does not tell us
    how much of the edge is recent.
- On index funds (SPY, QQQ) the same rule has kept working since 2008 according to several vendor
  backtests, for example about 10.7% a year on QQQ since 2000 from 321 trades **[Checked today, but a
  vendor claim relayed by a news aggregator; not independently confirmed]**.
- Why it should work at all: buying what others are forced or frightened to sell is "providing
  liquidity", and the market pays for that service. Nagel (2012) shows the payment rises when the
  VIX (the market's fear gauge) is high **[Checked today]**. This is also why the losses come all at
  once: in a real crash the dip keeps dipping.
- Entry at the close versus the next open: I found no clean published number. Lou, Polk and Skouras
  (2019) report that reversal profits are earned overnight rather than during the day **[Checked
  today, abstract only]**, which supports your decision to enter before the close. Our own ten-year
  data can measure this directly in an afternoon (section 2.2).

**What to expect here [My estimate].** Gross 0.3-0.45% per trade on large liquid stocks; after 10
basis points per side, 0.1-0.25%. With 10 positions of 10% and a 3-4 day hold, about 250-400 trades a
year, giving roughly 3-8% a year above cash, with a drawdown of 15-25% in a bad sell-off. On small and
mid caps the average may be higher but so are costs and collapse risk.

**Is the implementation faithful?** Mostly yes. The rule (close above the 200-day average, RSI(2)
below 5, exit above the 5-day average) matches the commonly cited version. Differences that matter:

| Topic | What we do | My view |
|---|---|---|
| Universe | Every stock with price at least $5 and 1M shares a day, limited to "complete" stocks | The evidence is on large liquid stocks and index funds. Test two declared tiers: the 500 most traded by dollar volume, and the next 1,000. Drop the complete-stocks rule (section 2.1). |
| Entry time | 9.2.0 decides on the 15:00 hourly close and fills at the 15:00 bar's open plus slippage | Sound and free of look-ahead (I read `DecisionHourDailyBars.cs` and the runner). A second timing is worth studying: decide at 15:00, send a market-on-close order (an order that fills in the closing auction), fill at the official close. Alpaca accepts these until 15:50 New York time **[Checked today]**. It is exactly reproducible live and closer to the book. Study it as a timing option, not as another strategy version. |
| Ranking when full | Lowest RSI(2) first | The deepest drops are more often bad-news collapses. Keep it, but let the equal-weight study show whether deeper RSI really pays more. |
| Stop (7.x) | 2xATR stop, 2.5R target, 5-bar cap | A tight stop works against a strategy whose trades usually get worse before they get better; your own October diagnosis found the ATR stop removes the edge. Replace it with protections that do not cut the bounce: small positions, a time limit, a far "disaster" stop (for example 20% or 5xATR), and a market filter. |
| News and earnings | None in Connors; a 120-hour news veto in snapback | Chan (2003) found reversal mainly after moves without news **[From memory; cited in your own files]**. Skip entries within a day of an earnings release and test the no-news veto as one declared option. |
| Benchmark | SPY over the same holding days | Good. Also compare with the equal-weight return of the eligible stocks on the same days (your `SignalStudyAnalyzer` already does this), because a small-cap universe does not move like SPY. |

**The one-year test.** With roughly 400 trades, a per-trade spread of about 6%, and trades bunched on
the same days, the average is known only to within about plus or minus 0.4% **[My estimate]**. The
plausible true edge is 0.2-0.4%. So:

- a strategy with a real edge of 0.3% shows an average at or below zero, and is retired, about 24% of
  the time;
- a strategy with no edge at all shows a positive average, and is kept, about 50% of the time.

That test costs the freshest year of data and settles nothing. Seven development years on about
1,250 stocks give 15,000-25,000 trades and can see a 0.2% edge clearly.

**Verdict:** worth pursuing, as the long-only liquid-stock reversal family, with modest expectations.
Judge it on the ten-year signal study first.

### 1.2 Connors short (2.x), snapback long and short, recovery

- **Connors short and snapback short: drop.** The published support for the short side is weak (the
  overview says so itself). A short with no stop has unlimited loss. The stocks these rules pick
  (below trend but spiking, or 10% above their 20-day average) are the ones most likely to be
  expensive or impossible to borrow. Alpaca charges no borrow fee on easy-to-borrow shares but
  hard-to-borrow shares need a paid "locate" and a daily fee **[Checked today]**; none of that is in
  the simulator. The complete-stocks rule also removes every stock that ever jumped 90% overnight,
  which is the short's worst case. In the volatile-list test the news veto removed 86 short setups
  against 6 long ones: sharp rises in these stocks are mostly news, where reversal is not expected.
- **Snapback long: merge into the reversal study.** RSI(14) below 30 and 10% below the 20-day average
  is a stricter cousin of the Connors rule and produced about 2 long trades in six months. The papers
  it cites rank all stocks every week by last week's return; a fixed threshold is a loose proxy.
  The published net result for the ranked version is 0.30-0.50% a week in large stocks for 1990-2009
  under institutional cost assumptions (de Groot, Huij and Zhou 2012) **[Checked today, abstract and
  summaries]**, while Blitz and co-authors find that single short-term signals earn more than 6% a
  year before costs and lose money after 0.25% per trade, surviving only when combined and traded
  sparingly **[Checked today, Robeco summary]**. The lesson is the same as for Connors: real, thin,
  and cost-bound. Keep snapback's one distinctive piece, the no-news veto, as an option inside the
  single reversal study.
- **Recovery with news avoidance:** it exists only as snapback's control. Retire it together with
  snapback as separate artifacts once the merged study is declared.

### 1.3 Opening-range breakout and "stocks in play"

**What the paper reports [Checked today, from the authors' summary and CXO Advisory].** Zarattini,
Barbon and Aziz (2024): more than 7,000 US stocks, 2016-2023; open above $5, 14-day average volume at
least 1M shares, 14-day ATR above $0.50; each day keep the 20 stocks with the highest opening
five-minute volume relative to their own 14-day norm (at least 100%); stop order at the five-minute
range in the direction of the first candle; stop 10% of ATR from the fill; exit at the close; risk 1%
per trade; up to 4x leverage; $25,000 start; commission $0.0035 per share. Headline: more than 1,600%
total, 41.6% a year, Sharpe ratio 2.81 (the Sharpe ratio is return per unit of risk; the S&P 500 is
about 0.8), alpha 36% a year. **I could not find any slippage assumption in the sources I could
open; the full paper should be checked for this single point.**

**Replication [Checked today].** One QuantConnect re-creation covers 2016 only and reports a Sharpe
ratio of 2.4. In its discussion, users report that other years look worse, that about 17% of trades
win, that most trades are stopped out, and that live results differ from the backtest because of
when the stop order is placed. I found no independent multi-year replication with realistic fills.

**Why I think our result and the paper's differ.**

1. **Slippage in units of risk.** Take a $40 stock with an ATR of $1.60. The stop is $0.16 away
   (0.4%). Our 10 basis points per side is $0.04 in and $0.04 out: $0.08, half of the planned loss,
   on every trade. The paper's commission is $0.007 per share round trip: one twenty-fifth of the
   planned loss. Our cost setting is about twelve times the paper's. A system that wins 17-25% of
   the time and lives on a few large winners cannot give up half a unit of risk per trade.
2. **Slippage was not counted when "costs" were ruled out.** The statement that commission was under
   5% of each loss is true, but `opening-range-breakout.bt-v5.yaml` has `slippage_bps: 10` and
   `IntradayPortfolioExecution` applies it to fills. By my arithmetic it explains somewhere between
   a quarter and most of the October losses, depending on how often the position cap applied.
   The published trade files can settle this exactly: add up slippage times traded value per trade.
3. **Two thirds of trades were scored as losses by rule**, because entry and stop fell inside one
   five-minute candle. One-minute candles will still leave many undecided with a stop this close.
   You already download Alpaca's individual trade records for quiet intervals. Using those records
   for the entry candle only (about 2,500 small requests for the top-20 list) gives the true order
   of events and a measured entry price, instead of an assumption.
4. **No stock selection.** The paper credits the daily top-20 selection for the edge. Not applied
   yet, as you know.

**What a bar-based backtest cannot tell you.** Whether a stop order at 09:35 in a stock everyone is
watching fills 2 or 20 basis points away. That number decides the strategy. It can be measured from
quotes and trade records, or from small real orders. Alpaca's paper account will not measure it:
paper orders fill at the quoted price without checking how many shares were actually available
**[Checked today, secondary sources; Alpaca's own page says only that fills and liquidity
assumptions differ]**.

**Other facts that changed.** The $25,000 pattern-day-trader minimum was abolished: the SEC approved
the change on 14 April 2026, effective 4 June 2026, with a phase-in to 20 October 2027 **[Checked
today, FINRA Regulatory Notice 26-10]**. Check when Alpaca adopts it. Half the strategy is short
selling stocks in the news, which are often hard to borrow.

**Verdict:** the largest claimed return, the weakest independent support, the most engineering, and
the greatest sensitivity to an unmeasured cost. Keep it paused. When it resumes, do not build the
whole-market portfolio run first. Do a per-trade study on the ranked top-20 list with trade records,
at three slippage levels (0, 5 and 10 basis points per side), and continue only if the average
result at 5 is clearly above zero. **ORB 1.2.0** has no published source and lost; retire it.

### 1.4 What I would build instead, or as well

All return figures below are **[My estimate]** after costs, for $25k-$250k, without borrowing.

| # | Strategy | Why it should work | Best evidence | Realistic edge and frequency | Data needed (held?) | Main risks | Honest test |
|---|---|---|---|---|---|---|---|
| A | **Dip-buying on index funds** (SPY, QQQ, IWM, sector funds; Connors' own home ground) | Paid for buying when investors cut risk; an index cannot go bankrupt | Connors 2008; vendor backtests since; Nagel 2012 for the reason | 3-7% a year above cash; 10-25 trades a year per fund; in the market 15-25% of the time; costs near zero | Daily bars. Held for 2016-2026; add free history back to 1993 | Few trades, so slow to confirm; losses in fast crashes | 30 years, a small grid of neighbouring settings that must all work, last 2 years held back |
| B | **Stock reversal, long only, liquid, no-news** (Connors and snapback merged) | Same as A, per stock; larger but riskier | Concretum 2025; de Groot 2012; Blitz 2023 | 0.1-0.25% per trade net; 250-400 trades a year; 3-8% a year above cash | Held (daily 2016-2026, news store, earnings dates for recent period) | Bunched losses; survivorship; costs | Section 2.2 |
| C | **ETF trend and rotation core** (hold funds that are above their long-term average, rotate monthly among a dozen) | Investors react slowly; trends persist; the rule sidesteps long bear markets | Faber 2007; Moskowitz, Ooi and Pedersen 2012 **[From memory]** | Equity-like return with roughly half the drawdown; 10-30 trades a year; not "alpha" | Daily ETF bars; held for 2016+, longer history is free | Whipsaw years (2011, 2015-16, 2018, 2023) lag buy-and-hold | Cannot be proven in one year; rely on long history, keep the rule dull, judge by drawdown |
| D | **Post-earnings reaction drift** (your own Recipe A/B) | Prices under-react to a large, high-volume earnings move | Old evidence strong; Martineau 2022 finds classic drift gone in non-tiny stocks since 2006; 2025 papers dispute this **[Checked today; the 2025 papers not verified]** | 0.5-1.5% per trade over 2-8 weeks if it exists; 150-300 events a year | Needs historical earnings dates and times for 2016-2026: **not held**; about $20-50 a month from a fundamentals vendor **[price not verified]** | Small edge, one event type, gap risk | Per-event study on reaction size and volume, compared with same-day non-earnings movers |
| E | **Intraday momentum on SPY** (Zarattini, Aziz and Barbon 2024) | Dealers hedging options push the index further in the direction it already moved | Authors report 19.6% a year, Sharpe 1.33, 2007-2024, after costs **[Checked today; no result after publication found]** | Unknown after publication; one or two trades a day | Intraday bars for one symbol: easy from Alpaca | Same authors as ORB; uses leverage; needs a same-day paper driver | One symbol, tight spread, no borrow problem, no universe bias: far easier to test honestly than stock ORB |
| F | **Trend following on single stocks** (new all-time high, wide trailing stop) | A few stocks make most of the market's gains | Zarattini, Pagani and Wilcox 2025: 66,000 trades 1950-2024; under 7% of trades make most of the profit; authors say hard below $1M **[Checked today]** | Lottery-like with 20-40 positions | Survivorship-free daily data: not held | Needs hundreds of positions | Low priority for this account |

**Not worth building here:**

- **Overnight effect** (buy the close, sell the open): the gross effect is real, but two trades a day
  cost about as much as the effect pays. Use it only as a reason to enter reversal trades before the
  close.
- **Pairs trading and statistical arbitrage:** profits have shrunk steadily since the early 2000s and
  need cheap shorting on many legs **[From memory: Gatev and others 2006; Do and Faff 2010]**.
- **Index additions and deletions:** the effect has largely disappeared **[From memory: Greenwood and
  Sammon 2022]**.
- **Options and volatility products:** selling options is dip-buying with a worse worst case. Not
  before a stock strategy has passed.
- **Test lists of "volatile" or "interesting" stocks:** chosen with hindsight; results on them are
  not evidence.

### 1.5 Ranking

1. **First:** the ten-year signal study of the reversal family (B), together with the index-fund
   version (A). No new data, no new strategy versions, days of work.
2. **Second:** the ETF core (C) as the base layer for the money, and the earnings-reaction study (D)
   once earnings dates are bought.
3. **Third:** SPY intraday momentum (E); then stock ORB, only through the trade-record study in 1.3.
4. **Drop now:** Connors short, snapback short, ORB 1.2.0, the tight-stop Connors 7.x as a candidate
   (keep it only as a comparison line), recovery as a separate artifact, and all wishlist-based tests.

---

## 2. Architecture and process

### 2.1 Review of the design

**Good, keep.**

- One decision path for backtest and paper (`StrategyDecisionKernel`, shared planner and simulator).
- Frozen inputs with fingerprints; three identical re-runs gave identical results.
- The evidence catalog keeps each provider response and a completion record per request, so a missing
  bar can be told from a failed download. Few private projects do this.
- As-traded prices with explicit split handling; exchange calendar from data, including half days.
- Confirming a missing five-minute candle from the trade records before calling it quiet.
- Skip-and-continue with a named reason and a 5% alarm, instead of one stock stopping a run.
- Pass and fail rules written before the run; a retirement ledger; monthly, per-version reporting.
- The decision-hour code: it drops the 09:00 hourly bar that mixes pre-market trading, rebuilds the
  unseen first half hour from the daily bar for resting orders only, and never reads the final close.
- `SignalStudyAnalyzer` already contains the right test: at least 2,000 trades, return in excess of
  the eligible stocks' average, t of at least 3 with day clustering, a block bootstrap, a limit on
  one year's share of profit. **It has no caller outside its own tests.** This is the most valuable
  unused component in the repository.

**Risky or wrong.**

1. **The complete-stocks rule looks into the future.** `DailyScreenCommand.IncompleteReason` removes a
   stock for the whole window if it misses any session, has any recorded split, or has any overnight
   move to 55% or less, or 190% or more, of the prior close. Consequences:
   - a no-stop long can never hold a stock into a 45% overnight collapse, because such stocks were
     removed in advance;
   - a no-stop short can never be caught by a 90% overnight jump;
   - companies that later did a reverse split (usually after a long decline) are gone; so are stocks
     that were halted for a day or delisted.

   My rough estimate of the effect on the average is a few basis points per trade, perhaps a tenth of
   the edge; the effect on the worst trade and the drawdown is much larger, and those are the numbers
   the 40% drawdown rule reads. The same file already has the point-in-time versions
   (`split_in_lookback`, `SuspectInLookback`): exclude a stock only for the days after such an event.
   A real 45% gap in a held position should be taken as a loss at the as-traded open. The
   `suspected-splits.csv` output, with volume before and after, can separate true unrecorded splits
   (price and volume change by a simple ratio) from real crashes automatically. The skip-and-continue
   rule already protects the run from missing data, so the complete-stocks rule is not needed for
   that purpose.
2. **Survivorship.** Alpaca's inactive list misses about two thirds of known delisted companies.
   Accepting this is reasonable for a first look, not for a decision to trade. A survivorship-free
   daily history costs a few hundred dollars a year (section 2.2).
3. **The trial count is unknown.** `docs/operating-boundaries.md` says saved-plan backtests are not
   in the trial registry. Without the number of tries, no result can be adjusted for luck.
4. **One flat slippage number.** 10 basis points per side is too much for an S&P 500 stock at the
   close and too little for a $6 stock at 09:35. Costs should depend on the stock's own spread.
5. **Paper differs from the test** (item 8 in section 0). A limit entry order at the next open is
   filled more often on stocks that keep falling and missed on those that bounce at once; this is the
   classic reason live reversal results trail backtests. Check which limit price is used and what
   happens to unfilled orders.
6. **Strategy identity carries too much.** Connors 9.0.0, 9.1.0 and 9.2.0 are the same trading rule
   with a different price basis and timing. Each needed a new file, a catalog entry and an admission
   profile; there are now 15 strategy files and 10 admission profiles for four ideas. Keep three
   separate identities: the rule, the execution timing, and the price basis. One rule can then be
   studied under several timings without cloning it.
7. **Stale fields in run profiles.** `connors-whole-market-hourly-profile.yaml` still says
   `universe_source: wishlist`, "diagnostic six-month window", news veto settings and
   `order_type: bracket`. They do nothing in this run but will mislead a future reader.

**Over-engineered for the present stage.**

- About 155,000 lines of non-test C# and 94,000 lines of tests, a web desk, a mobile app, an earnings
  page and an Azure plan, with no strategy yet shown to have an edge. None of it is wrong. Its order
  is: the question "does this signal pay" comes first and needs a few hundred lines.
- A full portfolio replay through the candidate journal for every research question. The Connors
  one-year run needs a 4.1 million bar budget and a one-month trial to measure memory. The same
  question as a per-signal study over daily bars runs in minutes.
- The whole-market ORB path (new stock source, catalog bar reader, one-minute loading, 2,502 download
  requests) was built before a per-trade study showed whether the idea survives realistic costs.

### 2.2 Faster and more trustworthy research

1. **A two-stage funnel.**
   - Stage 1, the **signal study** (also called an event study): for every signal in ten years, follow
     it from entry to exit, subtract costs and the average return of all eligible stocks over the
     same days, and average by day. No portfolio, no slots, no journal. Wire `SignalStudyAnalyzer` to
     the daily bars in the evidence catalog. Report by year.
   - Stage 2, the **portfolio replay**, only for a signal that passed stage 1. It answers different
     questions: how much of the per-signal average survives ten slots, and what the drawdown is.
   - Stage 3, paper, with the same settings as stage 2.
2. **Declare the grid before looking.** One page per family: the primary setting, at most 8
   neighbouring settings, the pass rule. The primary setting decides; the neighbours must not
   contradict it. A result that works at RSI below 5 but not below 4 or 7 is luck.
3. **Count every try** in the trial registry, per family, and cap it (for example 10 per family).
   Raise the bar with the count; Bailey and Lopez de Prado's "deflated Sharpe ratio" is the standard
   method **[From memory]**.
4. **Development, holdout, paper.** For fixed published rules, heavy machinery such as purged
   cross-validation is not needed; that is for fitted models. What is needed: 2017-2023 to develop,
   2024 to October 2025 opened once, and November 2025 onward kept for last. Walk-forward testing
   (re-choosing settings each year on past data only) matters only if settings are tuned.
5. **Costs from measurement.** For each paper and live order record the price at the decision, the
   quote at submission, and the fill. The difference is the "implementation shortfall". After 100
   orders replace the flat 10 basis points with the measured figure by price and liquidity group.
   Until then report every result at 0, 5, 10 and 20 basis points per side and state the cost at
   which the edge is gone.
6. **Measure the survivorship effect instead of arguing about it.** Run stage 1 twice: on the
   complete-stocks list and on the point-in-time list. The gap is the bias, in your own data.
7. **Survivorship-free daily data.** Norgate Data's Platinum level includes delisted US stocks back
   to 1990 and past index membership, which also gives a true historical S&P 500 or Russell 1000
   list; sources put it at roughly $630-790 a year **[Checked today; sources disagree, confirm on
   Norgate's site]**. It is daily only and delivered through a Windows application, so getting it
   into the evidence catalog needs a small approved exporter. EODHD lists about 60,000 delisted US
   symbols through an ordinary web API at a lower price **[Checked today, vendor's own claim; quality
   not verified]**. Either would replace the largest unproven assumption in the swing work.
8. **Fewer, larger experiments.** No test on fewer than about 300 stocks or two years unless it is an
   engineering check, and label engineering checks as such so nobody reads the profit line.

### 2.3 What must exist before real money

| Area | Needed | State today (from the code I read) |
|---|---|---|
| Loss limits | Halt new entries after a daily, weekly and peak-to-trough loss limit; enforced in the order path | Parameters defined (`RSK-01`); no enforcing code found. Only "new stocks per day" and "open positions" are enforced |
| Kill switch | One action, usable from the phone, that cancels all orders and optionally closes all positions; tested monthly on paper | `ExecutionShutdownCoordinator` exists; I did not verify a flatten-all path |
| Protection when the app is down | A no-stop strategy has no order at the broker. Place a far "disaster" stop at the broker for every position, and alert when the app's heartbeat stops | Not present for no-stop strategies |
| Hosting | An always-on machine (a small rented server is enough; not the Azure cluster) with automatic restart and clock sync | Runs on a home Windows PC |
| Reconciliation | At start, before the open and after the close: broker positions, orders and cash against the app's records; any difference stops trading and alerts | `AccountReconciliationService` and a ledger exist; keep as a hard gate |
| Order types | For a last-hour reversal entry: market-on-close, or a marketable limit with a cap; never a plain limit that waits. Paper market-on-close orders have been reported to fill partially and then expire **[Checked today, Alpaca forum]**, so handle remainders | Paper profile uses limit entry |
| Sizing | Fixed fraction per position (at most 10% for stocks), total exposure at most 100% in the first year, no borrowing, long only | Present as configuration |
| Corporate actions | Splits, dividends and symbol changes for held positions and resting stops | Backtest rule exists (exit before a split); live handling not verified |
| Data failure | Stale or missing live data means no new entries, and exits fall back to a safe rule | Fallback to the historical pipeline exists; behaviour for exits not verified |
| Account facts | Confirm that live trading, margin and short selling are available from your country of residence, and how gains are taxed. Frequent trading has a tax cost no backtest shows | Not in the documents |
| Paper realism | Treat paper as a test of operations. Alpaca paper does not simulate dividends and fills without regard to available size **[Checked today]** | - |

---

## 3. Honest assessment

**Doing well.**

- Refusing look-ahead in decisions, fills and news timing, and writing down each remaining assumption.
- Counting unknown outcomes as losses rather than guessing.
- Reproducible runs and preserved evidence, including failures.
- Independent reviews that changed the design (monthly statistics, the SPY comparison, the
  equal-weight study, "one year can only reject").
- Walking away from the 25-stock list and from dozens of strategy versions.

**Doing wrong, or spending time badly.**

- **Testing too small, then reading the tables.** Months of 0-7 trades were reported group by group.
  Nothing could be learned from them, and the format invites seeing patterns in noise.
- **Building the road before checking the destination.** Large data paths and new versions were
  built for ORB and for the hourly Connors run before a cheap per-signal study said whether an edge
  exists.
- **The one-year preference.** It is the right length for checking execution and the wrong length for
  finding an edge of a few tenths of a percent per trade.
- **Design churn.** The Connors design was amended four times in two days, moving from ten years with
  a holdout to one year with none. Each change was reasoned, but the end state is weaker evidence
  than the starting plan.
- **Chasing the largest published number.** A Sharpe ratio of 2.8 from day trading is the claim most
  likely to shrink.
- **Product work ahead of evidence.** Desk layout, the phone app and the earnings page are useful to
  operate a strategy. There is no strategy to operate yet.
- **Paper trading a configuration nobody tested**, under an authorization I could not find recorded.

**The biggest ways to fool ourselves, in order.**

1. Choosing the stock list with knowledge of the future (complete-stocks, survivors, today's lists).
2. Many tries with no count, then believing the best one.
3. A cost number that is assumed, especially where the stop is close.
4. A long-only strategy judged in a rising year; always subtract the market's own return.
5. Trusting paper profit and loss as evidence of edge or of fill quality.
6. No rule for when the search itself stops. "Retain research" can go on for ever.

---

## 4. A concrete plan

### 4.1 Next two weeks (no new data, no new strategy versions)

1. **Let the November 2025 engineering check finish; do not start the other eleven months.** Record
   that only November 2025 was viewed.
2. **Change the stock list rule** to point-in-time: price, volume and history floors from earlier
   sessions, a look-back exclusion after a split or suspected split, no whole-window completeness.
   Two declared tiers by 20-day dollar volume: top 500, and 501-1,500.
3. **Wire the signal study to the daily bars** and run the reversal family on 2017-2023:
   - primary setting: top-500 tier, close above the 200-day average, RSI(2) below 5, exit above the
     5-day average, entry at the next open, 10 basis points per side;
   - declared neighbours: RSI below 10; the 501-1,500 tier; entry at the same day's close as an upper
     bound; skip within one day of earnings where dates exist; the no-news veto where news exists;
   - **pass** (the analyzer's existing defaults): at least 2,000 trades, average return above the
     eligible stocks' average by at least 0.15% per trade after costs, t of at least 3 by day, at
     least 70% of years positive, no year above 35% of the profit, still positive at double cost;
   - **retire the family for single stocks** if the primary setting is below 0.10% or t is below 2.
4. **Run the same study on index funds** (SPY, QQQ, IWM, DIA and the sector funds) with the held
   2016-2026 bars, as a first look; mark it provisional until longer history is added.
5. **Measure the bias:** repeat step 3 on the complete-stocks list and report the difference.
6. **ORB accounting check:** from the plan 105 trade files, total the slippage in dollars and state
   what share of each group's loss it was. No new run.
7. **Paper run:** leave it running as an operations drill, label it so, and record decision price,
   limit price, fill price and unfilled orders for every signal. Record its authorization in the
   catalog or the governance document, or stop it.
8. **Decide on the survivorship-free data purchase.**

### 4.2 Next three months

| Step | Condition to start | Pass rule | If it fails |
|---|---|---|---|
| Open the 2024 - October 2025 holdout for the reversal family | Passed 4.1 step 3 | Positive excess after costs, t of at least 2, same sign in both tiers | Retire the single-stock version; keep the index-fund version only if it passed on its own |
| Portfolio replay with last-hour entry on the hourly year (Nov 2025 - Oct 2026) | Holdout passed | Not a profit test. Measures how much of the per-signal average ten slots capture, and the drawdown | Adjust slots and sizing, not the signal |
| Paper shadow of exactly that configuration | Replay done | At least 3 months and 150 trades; per-trade paper result within a stated tolerance of the replay for the same signals; measured shortfall no worse than the cost assumption plus 5 basis points | Fix execution, or stop |
| ETF core study and build | Any time | Drawdown at most about half of buy-and-hold over the long history; no tuning | Hold cash or a plain index fund instead |
| Earnings-reaction study | Earnings dates bought | Same form as 4.1 step 3, per event | Shelve |
| Safety work from section 2.3 | Any time | Each item demonstrated on paper | No live trading |
| Trial registry for every backtest; rule, timing and price basis as separate identities | Any time | - | - |

Optional in this period: the SPY intraday study (one symbol), and the ORB trade-record study.

### 4.3 Six to twelve months

1. **First real money, small.** Only a configuration that passed development, holdout and paper.
   Start with the smaller of $10,000 or 10% of the intended capital, half-size positions, long only,
   no borrowing, for three months.
2. **Scale in steps:** to 50% and then 100% of intended capital, each step only if the measured
   shortfall stays within the modelled cost plus 5 basis points and the live trades track the replay
   of the same signals.
3. **Structure of the money [my suggestion]:** 60-70% in the ETF core, 20-30% in the reversal sleeve,
   up to 10% in an event sleeve if one passes, all in one account with one set of limits.
4. **Limits fixed in advance:** stop new entries for the day at -2%, for the week at -4% (your own
   registry values); halt a sleeve at -10% from its peak and review; halt everything at -15%.
5. **Expectations [My estimate].** Reversal sleeve on its own capital: 3-8% a year above cash,
   drawdown 15-25%. Whole account as structured above: about 6-12% a year after costs, drawdown
   12-20%, Sharpe ratio 0.6-0.9. In a strong market year a plain index fund will beat it. If someone
   shows a backtest of this system with 30% a year, the backtest is wrong.

### 4.4 Decision points where the course should change

- **Reversal study fails on ten years:** stop all single-stock mean-reversion work (Connors,
  snapback, recovery). Do not rescue it with filters found after the fact.
- **Nothing passes development and holdout within three months:** stop the search for price-rule
  edges. Run the ETF core alone. The remaining differentiated asset is the news and earnings text
  store; that is a different, slower research programme.
- **ORB trade-record study at 5 basis points is not clearly positive:** retire ORB for good.
- **Live shortfall is more than twice the model, or live trades stop tracking the replay:** go back
  to paper.
- **A family reaches its trial cap without a pass:** it is closed.

### 4.5 What I am unsure of

- Whether the ORB paper models slippage. I could not open the full text. This one fact changes how
  the paper should be read.
- How much of the stock-level RSI(2) edge is left after about 2015. Concretum gives no period
  breakdown; our own ten-year study will answer it.
- The size of the survivorship effect on dip-buying. I found no published figure; step 5 of 4.1
  measures it.
- Whether Alpaca's daily bar close equals the official closing-auction price for every listed stock.
  This matters if market-on-close fills are modelled from daily bars.
- Vendor prices and Alpaca's current margin and day-trading terms; I marked each where I could not
  confirm it.
- My return estimates. They are informed guesses with wide error, given to set expectations, not to
  be used as targets or pass rules.

---

## 5. Short answers to the overview's research questions (section 7 of the overview)

1. **ORB paper:** sample, rules, costs and headline figures as in 1.3. Results by year, win rate,
   average result per trade, long versus short and the "without selection" comparison: not available
   in the sources I could open. Replication: one, for 2016 only, with user reports of weaker other
   years.
2. **ORB expectations May-October 2026:** not predictable from published figures. Under our 10 basis
   points per side each trade starts about 0.3-0.7 of its planned loss behind; I expect a loss under
   that setting whatever the selection does. The share of risk taken by commission is small at any
   account size; the spread and slippage are what matter, and they do not shrink with account size.
3. **Stop at 10% of ATR:** it sits inside ordinary opening noise, as your October analysis found. It
   produces a low win rate with rare large winners, and it makes the result depend on fill precision.
   I found no evidence for or against wider stops for this rule.
4. **Connors RSI(2):** see 1.1. Small and mid caps: no independent study found. Short side: no
   support found.
5. **Short-term reversal:** real, concentrated in news-free moves and in stressed markets, thin after
   costs; a fixed threshold is a loose proxy for the ranked version. See 1.2.
6. **Decay:** 26% out of sample, 58% after publication; near zero on average after costs. See 1.0.
7. **Sample size:** your own formula in the overview is right. Group by day, not by trade. For the
   reversal family about 15,000-25,000 trades over seven years; for ORB at least 2,000 trades.
8. **Pitfalls:** section 3, "ways to fool ourselves".
9. **One-minute ordering:** sound as designed, but with a stop this close many candles will remain
   undecided. Use the trade records for the entry candle.
10. **Price basis:** as-traded prices with look-backs corrected for splits known at the time is the
    accepted practice. Excluding the days after a split, as you chose, is a valid simpler version.
    Large special dividends and spin-offs can still shift a 14-day range; count them and exclude
    those days the same way.
11. **Spreads at the open:** I have no measured figure and would not assume one. Measure it from
    quotes for the selected stocks; report results at 0, 5 and 10 basis points.
12. **Same-day paper driver:** entry stop orders placed at 09:35 with the protective stop attached at
    the broker at fill; exit by a market order a few minutes before the close, or market-on-close
    sent before 15:50; cancel everything on a trading halt or when the data stream is more than a few
    seconds behind; a hard cap on total exposure checked before each order; a flatten-all switch.

---

## Sources

Opened today (12 November 2026):

- Zarattini, Barbon, Aziz, "A Profitable Day Trading Strategy For The U.S. Equity Market" (authors' summary): https://concretumgroup.com/a-profitable-day-trading-strategy-for-the-u-s-equity-market/
- CXO Advisory summary of the same paper (rules, screen, commission): https://www.cxoadvisory.com/individual-investing/intraday-trading-of-overactive-stocks-via-opening-range-breakout/
- QuantConnect re-creation and discussion: https://www.quantconnect.com/research/18444/opening-range-breakout-for-stocks-in-play/
- Concretum, "Opportunity-set bias in mean reversion trading systems": https://concretumgroup.com/opportunity-set-bias-in-mean-reversion-trading-systems/
- CXO Advisory, notes on Connors and Alvarez, *Short Term Trading Strategies That Work*: https://cxoadvisory.com/technical-trading/a-few-notes-on-short-term-trading-strategies-that-work
- Vendor claim on the QQQ RSI rule (relayed): https://www.pulse.bot/stock-trading/news/larry-connors-rsi-strategy-still-performing-well-10e9917d-7a64-4f63-89af-177f2f68a18a/
- Nagel, "Evaporating Liquidity": https://www.nber.org/papers/w17653
- de Groot, Huij, Zhou, "Another look at trading costs and short-term reversal profits": https://econpapers.repec.org/RePEc:eee:jbfina:v:36:y:2012:i:2:p:371-382 and summary https://www.cxoadvisory.com/?p=6251
- Blitz and others, "Beyond Fama-French Factors: Alpha from Short-Term Signals" (Robeco summary): https://www.robeco.com/en-uk/insights/2022/05/beyond-fama-french-alpha-from-short-term-signals
- Chen, Velikov, "Zeroing In on the Expected Returns of Anomalies": https://www.cambridge.org/core/journals/journal-of-financial-and-quantitative-analysis/article/zeroing-in-on-the-expected-returns-of-anomalies/945133D5A3ECEEAF466AEE91551FD225
- Lou, Polk, Skouras, "A tug of war: overnight versus intraday expected returns": https://personal.lse.ac.uk/polk/research/TugOfWar.pdf
- Martineau, "Rest in Peace Post-Earnings Announcement Drift": https://nowpublishers.com/article/Details/CFR-0122 and the later dispute: https://anderson-review.ucla.edu/is-post-earnings-announcement-drift-a-thing-again
- Zarattini, Pagani, Wilcox, "Does Trend Following Still Work on Stocks?": https://concretumgroup.com/does-trend-following-still-work-on-stocks/
- Zarattini, Aziz, Barbon, "Beat the Market: An Effective Intraday Momentum Strategy for S&P500 ETF (SPY)": https://concretumgroup.com/beat-the-market-an-effective-intraday-momentum-strategy-for-sp500-etf-spy/
- FINRA Regulatory Notice 26-10 (end of the pattern-day-trader rule): https://www.finra.org/sites/default/files/2026-04/Regulatory-Notice-26-10.pdf and https://international.schwab.com/story/sec-approves-scrapping-25000-day-trader-minimum
- Alpaca orders, including closing-auction orders and the 15:50 cut-off: https://docs.alpaca.markets/us/docs/orders-at-alpaca.md
- Alpaca forum, paper market-on-close partial fills: https://forum.alpaca.markets/t/paper-moc-orders-partially-fill-remainder-expires-same-setup-on-live-works/19155
- Alpaca margin and short selling: https://docs.alpaca.markets/docs/margin-and-short-selling ; hard-to-borrow locates: https://alpaca.markets/blog/htb-trading-api-locates/ ; no fee on easy-to-borrow: https://alpaca.markets/blog/zero-borrow-fees-on-short-selling-etb-stock-shares-alpaca-trading-api/
- Alpaca paper trading: https://docs.alpaca.markets/docs/paper-trading
- Norgate Data packages: https://norgatedata.com/stockmarketpackages.php and https://norgatedata.com/data-content-tables.php
- EODHD delisted data: https://eodhd.com/financial-apis/review-delisted-stock-companies-data

Cited from memory, not re-opened today:

- McLean, Pontiff (2016), "Does Academic Research Destroy Stock Return Predictability?", Journal of Finance.
- Harvey, Liu, Zhu (2016), "...and the Cross-Section of Expected Returns", Review of Financial Studies.
- Bailey, Lopez de Prado (2014), "The Deflated Sharpe Ratio", Journal of Portfolio Management.
- Chan (2003), "Stock price reaction to news and no-news", Journal of Financial Economics.
- Faber (2007), "A Quantitative Approach to Tactical Asset Allocation"; Moskowitz, Ooi, Pedersen (2012), "Time series momentum".
- Gatev, Goetzmann, Rouwenhorst (2006) and Do, Faff (2010) on pairs trading; Greenwood, Sammon (2022), "The Disappearing Index Effect".

Repository files read: the overview of 10 November, `AGENTS.md`, `docs/operating-boundaries.md`, the
Connors and ORB designs, `docs/research/volatile-core-strategies-2026-11-09.md`,
`docs/research/post-earnings-swing-research.md`, `configs/strategy-catalog.json`, the Connors, snapback
and ORB strategy files, `configs/paper/alpaca-paper.yaml`, the Connors hourly profile,
`DecisionHourDailyBars.cs`, `BacktestRunner.DecisionHour*.cs`, `ConnorsRsi2SignalAnalyzer.cs`,
`DeterministicExecutionSimulator.cs` (first part), `DailyScreenCommand.cs` (split and completeness
rules), `SignalStudyAnalyzer.cs` (first part), `ProductionParameterRegistry.cs` (risk parameters) and
`LiveRunner.ProcessTicker.cs` (order submission).
