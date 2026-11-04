# Post-Earnings Swing Research

**Date:** 4 November 2026
**Status:** Research note only. No code, configuration, data or backtest was changed or run.
**Scope:** Long-side swing trades (days to about three months) in US-listed stocks after quarterly earnings reports, built as a fixed-rule C# recipe in TradingFlow.

Tags: **[Published]** academic research; **[Practice]** what traders and firms do, often untested lore; **[Inference]** my judgement, not tested here. Sources with links are in Appendix A.

---

## 1. Bottom line

1. **The textbook signal is mostly gone in liquid stocks.** "Post-earnings announcement drift" (PEAD) means that after a company beats or misses analyst forecasts, its price keeps moving the same way for weeks. Ranked by the size of the beat, this drift has been absent outside the smallest 20% of stocks since about 2006, and costs absorb most of what remains. **[Published]**
2. **The market's own reaction is a stronger signal than the reported numbers.** A large reaction-day return, especially on heavy volume, has predicted further movement the same way, and held up better in large stocks. **[Published]** That evidence ends around 2009–2013 and needs retesting. **[Inference]**
3. **Build one shared event and two entries.** The event: a reaction session that was large for this stock, on heavy volume, closing near its high. **Recipe A** buys the next open. **Recipe B** buys only if the price later trades above the reaction-day high. In both, the stop is the reaction-day low, with a time limit, a trend exit, and an exit before the next report. **[Inference]**
4. **One year can find bugs and rule out a large edge. It cannot prove a realistic one.** It gives roughly 150–300 events in one market climate, and a realistic edge (about 1% per trade) is smaller than the noise of that sample. **[Inference, Appendix C]**
5. **The live "possible breakout" label measures the wrong thing.** It relies on 5-minute indicators and thin after-hours volume, so it flickers. It should ask whether the market repriced the stock and whether the price is holding. It should use daily units and stages that only move forward, settled at the reaction-session close (Section 7). **[Inference]**

---

## 2. The problem in plain words

A report brings two new things: the numbers compared with analyst forecasts, and the price reaction once traders read the whole release. If the price only partly adjusts on the reaction day, a trader who buys afterwards can capture the rest. Researchers call this rest **drift**. Three signals are often blurred together:

- **Earnings surprise**: actual earnings minus the analyst forecast, often scaled by how variable past surprises were. The scaled version is **SUE**, "standardized unexpected earnings".
- **Earnings announcement return (EAR)**: the stock's return over about three days around the report, minus the market's return over the same days.
- **Gap**: the jump between the last price before the release and the first regular-session price after it.

---

## 3. What the published evidence shows

The full evidence table is in Appendix B. In short:

- **The drift was real.** Stocks with the best earnings surprises beat those with the worst by about 4% over 60 trading days (Bernard & Thomas 1989, 1990). **[Published]**
- **It lived in hard-to-trade stocks and is now gone in liquid ones.** Chordia et al. (2009) find that trading costs absorb 70–100% of it. Martineau (2022) finds that since 2006 the analyst-based surprise does not predict returns outside the smallest 20% of stocks. Claims that it has returned depend on those smallest stocks (Subrahmanyam 2025, working paper). **[Published]**
- **The price reaction carries more than the numbers.** Ranking on EAR earned about 6–7.5% a year from 1987 to 2004, was largely independent of SUE, and survived, weaker, in the 1,000 largest firms (Brandt et al. 2008, working paper). Large price jumps at reports kept drifting for about three months (Zhou & Zhu 2012). **[Published]**
- **After costs, large caps are marginal.** Net of costs, roughly 0.2–0.3% a month long-short, not statistically reliable (Novy-Marx & Velikov 2016). The average published anomaly earns close to nothing after costs and after its publication date (Chen & Velikov 2023). **[Published]**
- **Helpful conditions:**
  - heavy announcement volume (Garfinkel & Sokobin 2006; Gervais et al. 2001);
  - a revenue surprise in the same direction as the earnings surprise (Jegadeesh & Livnat 2006);
  - a real news cause (Chan 2003; Savor 2012);
  - investor distraction, such as busy reporting days or Friday releases (Hirshleifer et al. 2009; DellaVigna & Pollet 2009).

  **[Published]**

**What this means [Inference]:** do not rank liquid stocks by earnings beat %. Test the narrower question: after a large, high-volume positive earnings reaction, does a liquid stock keep rising for 2–8 weeks by more than costs and the market? The evidence is old and comes from long-short portfolios, which buy winners and short-sell losers. Expect a small edge.

---

## 4. How practitioners run it

**Quant firms [Practice, partly Published]:**

- They score every reporting company on surprise and reaction, enter around the second day after the report, and hold the top group for about one quarter in large diversified books (Quantpedia's version of Brandt et al.).
- They cut trading by keeping names they would not newly buy (Novy-Marx & Velikov's "buy/hold spread").

**Discretionary swing traders [Practice; practitioner lore, no independent test with costs]:**

- **Episodic pivot** (Pradeep Bonde / Stockbee; Kristjan Kullamägi / "Qullamaggie"):
  - Setup: an unexpected catalyst, mainly earnings, in a neglected stock, with a 10%+ gap and very heavy early volume.
  - Entry: a break above the opening-range high (the high of the first 1, 5 or 60 minutes).
  - Stop: the day's low.
  - Exit: trail with the 10- or 20-day moving average.
  - Risk: 0.25–1% of the account per trade.
- **Buyable gap-up** (O'Neil school, Morales & Kacher):
  - The gap must be at least 0.75 × the 40-day average true range (ATR, the typical daily high-to-low range including overnight gaps).
  - Volume must be at least 1.5 × average, in an uptrend, and the stock must hold above the gap-day low.

**Common risk practice [Practice]:**

- Size each trade from the stop distance.
- Expect gaps through stops.
- Do not hold through the next report by accident. Part of the published EAR return arrives there (Brandt et al.), and so does much of the gap risk.
- Capacity is no constraint at this size. Crowding is: the effect weakened as computerised trading grew after 2001 (Martineau).

---

## 5. What TradingFlow should build

### 5.1 Shared event: "Strong earnings reaction" (catalyst required, one shot)

| Layer | Rule | Basis |
|---|---|---|
| Catalyst | An earnings release for this ticker. Its release time must resolve to one **reaction session**, the first regular session that opens after the release. If the provider timing and the bars disagree, drop the event and count it as ambiguous | Doctrine (catalyst required); Chan 2003 |
| Stock filter | Prior close ≥ $10 as traded. 50-day average dollar volume ≥ $20M. At least 63 completed daily sessions (ATR and average volume need them; a frozen design choice) | Chordia 2009; operating boundaries |
| Size of reaction | Reaction-session close versus the last pre-release close, minus SPY's return, ≥ 2 × ATR(14). ATR is computed only from bars before the release | Brandt 2008 (EAR); Zhou & Zhu 2012; O'Neil uses 0.75 × ATR40 for the gap alone |
| Participation | Reaction-session volume ≥ 2 × the 50-day average daily volume | Garfinkel & Sokobin 2006; practice |
| Holding the move | Reaction-session close in the upper half of that session's range: (close − low)/(high − low) ≥ 0.5 | Practice |

Earnings and revenue surprise are **recorded as a reporting split, not a gate**, until a point-in-time record proves what the provider showed at the time. The price reaction comes from bars and cannot be revised later.

### 5.2 Recipe A: reaction-close continuation (daily)

- **Entry:** market order at the next session's open, after the reaction bar has completed.
- **Initial stop:** the reaction-session low. If it is more than 3 × ATR away, the planner cuts the share count and never moves the stop closer.
- **Exit, whichever comes first:**
  - the stop;
  - after 10 sessions, two consecutive daily closes below EMA20 (the 20-day exponential moving average). This already exists as `RequireConfirmedEma20Exit`;
  - a 40-session time limit;
  - the close of the session before the next scheduled report.
- **Sizing:** fixed account risk per trade (for example 0.5%), capped by maximum position value, in a shared capital pool. When more events fire than there are slots, rank them by reaction size in ATR units.

### 5.3 Recipe B: reaction-high breakout (episodic-pivot style, 1-hour)

- **Event:** same as 5.1.
- **Trigger (one shot):** within 5 sessions after the reaction session, the first regular-session trade above the reaction-session high. This is a buy-stop order one tick above that high.
- **Cancellation:**
  - The event is used up if a daily close falls below the reaction-session low before the trigger.
  - The event expires after 5 sessions.
  - It is never re-armed.
- **Stop, exits and sizing:** same as Recipe A.
- **Why both:** A tests whether the reaction predicts drift at all. B tests whether waiting for price confirmation helps. If A fails, a B pass is probably luck. **[Inference]**

### 5.4 Components, data and look-ahead traps

**Components (Appendix E):**

- Already exist: ATR, EMA20 and volume indicators, the daily screen, the two-close EMA20 exit, a maximum hold in bars, the shared planner and the event-study tool.
- Missing:
  - a historical earnings event source for backtests;
  - a resolver from release time to reaction session;
  - price levels fixed at the event and carried to later bars;
  - a close-location filter;
  - an exit before the next report.

**Data:**

- Finviz earnings history (back to 2016 if obtainable), with raw JSON and download time.
- Alpaca SIP bars as traded, with corporate actions:
  - daily bars, and SPY daily bars;
  - 1-hour bars;
  - 5-minute bars from each fill onward.
- Alpaca news, to confirm release times.
- **Gap:** stop-protected strategies are judged on 5-minute execution (1 November rule), but the 5-minute window covers only 6 months. Extend it to the full year, or label the first half of the swing results as daily-bar estimates only.

**Main traps (all eight in Appendix E):**

- Finviz surprise values may have been revised after the report day.
- Before-open and after-close labels can be wrong.
- Filters use as-traded prices; levels use only splits known at the time.
- Gaps through stops fill at the opening price.

---

## 6. What one year can and cannot prove, and the frozen pass/fail rules

**Can:** find defects in data, timing and the simulator; show whether the event fires at a sensible rate; reject a large edge (more than about 2% per trade).

**Cannot:**

- separate a realistic edge (0.5–1% per trade after costs) from zero, because the year holds only four reporting seasons in one market climate;
- meet the existing `SignalStudyPassRules` defaults (2,000 trades, 500 signal dates, 70% positive years).

A one-year result therefore ends at most in `RETAIN_RESEARCH` or a decision to extend the test to 2016–2026, never in paper promotion. **[Inference]**

**Freeze these in the trial registry before any result is read:**

- **Variants:** Recipes A and B with the parameters above and no parameter search. That is two variants.
- **Periods:**
  - Events in months 1–6: development.
  - Events in month 7: dropped as a gap, because their trades would run into the holdout.
  - Events in months 8–11: holdout, used once.
  - Month 12: exits only.
- **Costs:** a modelled spread and slippage for each side, and gaps through stops filled at the open.
- **Benchmarks:** return over the same holding period versus (a) SPY and (b) a control group of same-season reporters with a positive but below-threshold reaction.
- **Pass (all required):**
  - at least 150 development trades and 60 holdout trades;
  - at least 100 distinct companies, and no company above 5% of trades;
  - development mean excess return after costs ≥ +1.0% per trade, with t ≥ 2.0. Trades are grouped by entry week for the standard error, because trades entered the same week move together;
  - holdout mean > 0 and at least half the development mean;
  - mean positive in at least 3 of the 4 seasons;
  - the 5 best trades provide at most 50% of net profit;
  - Recipe B beats A only if it does so in both periods.
- **Outcome:** a fail is `REJECT` for that recipe. A pass earns a multi-year rerun from 2016 with the same frozen rules.

---

## 7. The live earnings monitor label

**Judgement on the current rule [Inference]:**

- A positive result plus a price above the pre-release high is sensible.
- The rest does not suit a swing decision:
  - **EMA10 > EMA20 and MACD > 0 on 5-minute bars** describe the last hour or two. A normal pause after a gap switches them off, as happened with OUST. The doctrine allows such indicators only as permissions, never as the deciding condition.
  - **Same-time-of-day volume in after-hours slots** compares against tiny or empty slots, so it is often uncomputable or noise. After-hours prices on report days do carry real information (Jiang et al. 2012).
  - **"Positive" means any non-negative surprise.** Surprise percentages are unstable when the estimate is near zero; +87% can be a few cents.
- The label is recomputed every minute and can switch on and off within a session. A swing trader needs a verdict that settles.

**Proposed label: stages that only move forward, each with a timestamp**

1. **Reported, reaction pending.** The result is out and the reaction session has not started.
2. **Strong early reaction (provisional).**
   - Price test: the last extended-hours price is at least 2 × ATR(14) above the pre-release close, and above the pre-release high.
   - The label states openly that after-hours volume is thin and this is not confirmation.
3. **Gap holding (provisional).** In the reaction session:
   - price is above the low of the first 30 minutes and at least 1 × ATR above the prior close;
   - cumulative volume is on pace for 2 × the 50-day average daily volume. This compares with the daily average, not same-time-of-day slots.
4. **Possible breakout (confirmed at the reaction-session close).** All event rules in 5.1 pass: the market repriced the stock on heavy volume and held the move into the close.
5. **Triggered.** Price trades above the reaction-session high within 5 sessions (Recipe B).
6. **Failed.** A daily close below the reaction-session low. This is the only way a confirmed label is withdrawn.

For OUST, the evening of 3 November would have shown stage 2 only if 7–9% was at least 2 ATRs for that stock. Its ATR was not computed here, because no local candles were found. The verdict would have come at the 4 November close, unaffected by 5-minute MACD dips. The 5-minute indicators can remain on the page as context only.

---

## Appendix A: Sources

- Ball, R., & Brown, P. (1968). An empirical evaluation of accounting income numbers. *Journal of Accounting Research* 6(2). [Published]
- Bernard, V., & Thomas, J. (1989). Post-earnings-announcement drift: delayed price response or risk premium? *Journal of Accounting Research* 27. Bernard & Thomas (1990), *Journal of Accounting and Economics* 13. [Published] Summary: https://en.wikipedia.org/wiki/Post%E2%80%93earnings-announcement_drift
- Chan, L., Jegadeesh, N., & Lakonishok, J. (1996). Momentum strategies. *Journal of Finance* 51(5), 1681–1713. https://ideas.repec.org/a/bla/jfinan/v51y1996i5p1681-1713.html
- Brandt, M., Kishore, R., Santa-Clara, P., & Venkatachalam, M. (2008). Earnings announcements are full of surprises. Working paper (June 2007 version read; 1987–2004 sample). https://www.anderson.ucla.edu/documents/areas/fac/finance/ear.pdf
- Livnat, J., & Mendenhall, R. (2006). Comparing the post-earnings announcement drift for surprises calculated from analyst and time series forecasts. *Journal of Accounting Research* 44(1), 177–205. https://econpapers.repec.org/RePEc:bla:joares:v:44:y:2006:i:1:p:177-205
- Jegadeesh, N., & Livnat, J. (2006). Revenue surprises and stock returns. *Journal of Accounting and Economics* 41, 147–171; and Post-earnings-announcement drift: the role of revenue surprises. *Financial Analysts Journal* 62(2). https://rpc.cfainstitute.org/research/financial-analysts-journal/2006/post-earnings-announcement-drift-the-role-of-revenue-surprises
- Garfinkel, J., & Sokobin, J. (2006). Volume, opinion divergence, and returns: a study of post-earnings announcement drift. *Journal of Accounting Research* 44(1), 85–112. https://www.biz.uiowa.edu/faculty/jgarfinkel/pubs/drift_JAR.pdf
- Gervais, S., Kaniel, R., & Mingelgrin, D. (2001). The high-volume return premium. *Journal of Finance* 56(3), 877–919. https://ideas.repec.org/a/bla/jfinan/v56y2001i3p877-919.html
- Chan, W. S. (2003). Stock price reaction to news and no-news: drift and reversal after headlines. *Journal of Financial Economics* 70.
- Savor, P. (2012). Stock returns after major price shocks: the impact of information. *Journal of Financial Economics* 106(3), 635–659. https://econpapers.repec.org/RePEc:eee:jfinec:v:106:y:2012:i:3:p:635-659
- Chordia, T., Goyal, A., Sadka, G., Sadka, R., & Shivakumar, L. (2009). Liquidity and the post-earnings-announcement drift. *Financial Analysts Journal* 65(4), 18–32. https://rpc.cfainstitute.org/research/financial-analysts-journal/2009/liquidity-and-the-post-earnings-announcement-drift
- Hirshleifer, D., Lim, S., & Teoh, S. (2009). Driven to distraction: extraneous events and underreaction to earnings news. *Journal of Finance* 64(5), 2289–2325. https://ideas.repec.org/p/pra/mprapa/3110.html
- DellaVigna, S., & Pollet, J. (2009). Investor inattention and Friday earnings announcements. *Journal of Finance* 64(2).
- Zhou, H., & Zhu, J. Q. (2012). Jump on the post-earnings announcement drift. *Financial Analysts Journal* 68(3). https://www.tandfonline.com/doi/abs/10.2469/faj.v68.n3.7
- Jiang, C., Likitapiwat, T., & McInish, T. (2012). Information content of earnings announcements: evidence from after-hours trading. *Journal of Financial and Quantitative Analysis* 47(6), 1303–1330. https://ideas.repec.org/a/cup/jfinqa/v47y2012i06p1303-1330_00.html
- Novy-Marx, R., & Velikov, M. (2016). A taxonomy of anomalies and their trading costs. *Review of Financial Studies* 29(1), 104–147. https://www.nber.org/papers/w20721
- Chen, A., & Velikov, M. (2023). Zeroing in on the expected returns of anomalies. *Journal of Financial and Quantitative Analysis* 58(3), 968–1004. https://www.federalreserve.gov/econres/feds/zeroing-in-on-the-expected-returns-of-anomalies.htm
- Hou, K., Xue, C., & Zhang, L. (2020). Replicating anomalies. *Review of Financial Studies* 33. https://www.nber.org/papers/w23394
- Martineau, C. (2022). Rest in peace post-earnings announcement drift. *Critical Finance Review* 11(3–4), 613–646. https://cfr.ivo-welch.org/published/papers/martineau2021rest.pdf
- Subrahmanyam, A. (2025). Keeping it simple: how can post-earnings return drift exist and not exist simultaneously? SSRN 5930255 (working paper). Summary: https://anderson-review.ucla.edu/is-post-earnings-announcement-drift-a-thing-again/
- Chan, K. F., & Marsh, T. (2024). Overnight post-earnings announcement drift and SEC Form 8-K disclosures. SSRN 4765828 (working paper; seen only through a secondary copy).
- Quantpedia. Post-earnings announcement effect (strategy summary of Brandt et al.). https://quantpedia.com/strategies/post-earnings-announcement-effect [Practice summary]
- Kullamägi, K. My 3 timeless setups. https://qullamaggie.com/my-3-timeless-setups-that-have-made-me-tens-of-millions/ [Practice]
- Bonde, P. (Stockbee), episodic pivot, as summarized at https://retailtradersrepository.substack.com/p/pradeep-bonde-episodic-pivots [Practice, secondary]
- Morales, G., & Kacher, C., buyable gap-up definition, as quoted at https://tw.tradingview.com/chart/GDOT/PpBPgj8G-GDOT-buyable-gap-up-BGU-example [Practice, secondary]

## Appendix B: Evidence table and numbers behind Section 3

| Finding | Evidence | Holds after costs today? |
|---|---|---|
| Prices underreact to earnings news | Ball & Brown 1968; Bernard & Thomas 1989, 1990: best-surprise stocks beat worst by roughly 4% over 60 trading days; about a quarter of the drift arrives at the next reports **[Published]** | Not in liquid stocks (next rows) |
| Drift lives in illiquid stocks | Chordia et al. 2009: 2.43% a month long-short in the least liquid fifth versus 0.04% in the most liquid; costs absorb 70–100% **[Published]** | No for traders who pay spreads |
| Drift ranked by earnings surprise is dead outside microcaps | Martineau 2022: no 60-day predictability since 2006 above the NYSE 20th size percentile, and since 2016 in microcaps **[Published]** | No |
| Revival claims depend on microcaps | Subrahmanyam 2025, 2001–2024: t = 2.18 with all stocks, 1.43 without microcaps **[Published, working paper]** | No |
| The price reaction predicts drift | Chan, Jegadeesh & Lakonishok 1996; Brandt et al. 2008: EAR ranking earns about 6–7.5% a year, concentrated at the next four reports, largely independent of SUE **[Published, working paper]** | Weaker but present in the 1,000 largest firms (to 2004); not tested recently |
| Same, after trading costs | Novy-Marx & Velikov 2016: in large caps, 0.26% a month (t = 1.67) for SUE and 0.20% (t = 1.44) for the three-day reaction **[Published]** | Marginal |
| Large price jumps drift for about 3 months | Zhou & Zhu 2012: 3.6% a quarter long-short, 1971–2009 **[Published]** | Not tested after 2009 |
| Revenue surprise adds information | Jegadeesh & Livnat 2006 **[Published]** | Unknown for recent large caps |
| Heavy volume goes with larger later returns | Garfinkel & Sokobin 2006; Gervais et al. 2001 **[Published]** | Unknown recently |
| A move with news continues; a move without news reverses | Chan 2003; Savor 2012 **[Published]** | Supports requiring the catalyst |
| Drift is larger when attention is low | Hirshleifer, Lim & Teoh 2009; DellaVigna & Pollet 2009 **[Published]** | Unknown recently |
| Recent drift may hide in overnight returns | Chan & Marsh 2024: overnight drift after extreme misses **[Published, working paper]** | Short side; not for this system |

Further numbers:

- **Martineau 2022** (1984–2019, analyst surprises, 60-day buy-and-hold returns):
  - Announcement-day prices became six times (non-microcaps) and three times (microcaps) more responsive to surprises between 1984–1990 and 2016–2019.
  - No drift in non-microcaps since 2006, or in microcaps since 2016.
  - Surprises measured against the same quarter a year earlier, without analyst forecasts, predict only up to 5 days in microcaps.
- **Brandt et al.** (1987–2004):
  - 60-day long-short returns are 2.8% when ranked on EAR and about 3% when ranked on SUE; at 120 days, 3.7% and 4.6%.
  - The EAR ranking earns about 3% in each of the next four three-day report windows.
  - In the 1,000 largest firms, the EAR spread at the next report falls to 0.9–1.4%, but the SUE spread is mostly negative.
  - The EAR effect grew from 1987–1995 to 1996–2004.
- **Novy-Marx & Velikov 2016** (value-weighted deciles, 1963–2013):
  - Before costs, monthly long-short returns are 0.72% for SUE and 0.91% for the three-day reaction (CAR3).
  - Large caps before costs: 0.54% for SUE and 0.60% for CAR3.
  - Large caps after costs: 0.26% (t = 1.67) for SUE and 0.20% (t = 1.44) for CAR3.
  - Microcaps after costs: 1.10% for SUE and 0.73% for CAR3, but microcaps are outside this system's filter.
- **Chen & Velikov 2023:** the average published anomaly earns about 4–20 basis points a month after costs and after its publication date. This is the realistic starting assumption for any published effect.
- **Zhou & Zhu 2012:** jumps are found from price movements over the three days around the report. On average 7% of reporting firms jump each quarter. The long-short portfolio earned 3.63% a quarter (1971–2009).
- **Chordia et al. 2009:** 2.43% a month in the least liquid fifth versus 0.04% in the most liquid; costs absorb 70–100%.

## Appendix C: Why one year is statistically thin

These are rough figures from my own judgement, not measured from TradingFlow data:

- Liquid universe: about 1,200–1,500 stocks, giving about 5,000 reports a year. The event rule in 5.1 might keep 3–6%, so about 150–300 trades.
- For 40-session holds after a big reaction, the spread of individual trade returns is probably about 10–15%.
- With 200 trades and a 12% spread, the standard error of the average is about 0.85%. A t-statistic of 2 then needs an average of about 1.7% per trade.
- Grouping trades by entry week, because trades in the same reporting season move together, raises that bar further.
- Published large-cap reaction-drift effects are about 0.2–0.6% a month for long-short portfolios. Over a two-month hold the long side might give roughly 0.5–1%.
- So a one-year test can detect only an edge two to three times larger than the published estimates, and cannot confirm a realistic one.
- This is why Section 6 treats a pass as permission to rerun on 2016–2026, not as evidence of an edge.

## Appendix D: Frozen parameter list

| Parameter | Value | Source |
|---|---|---|
| Minimum price (as traded, prior close) | $10 | Practice; liquidity |
| Minimum 50-day average dollar volume | $20M | Chordia 2009; inference |
| Minimum completed daily sessions | 63 | Inference (ATR and average volume need them) |
| Reaction size (close-to-close minus SPY) | ≥ 2.0 × ATR(14) | Brandt 2008; O'Neil 0.75 × ATR40 for gap only |
| Reaction-session volume | ≥ 2.0 × 50-day average | Garfinkel & Sokobin 2006; practice |
| Close location in reaction range | ≥ 0.5 | Practice |
| Recipe A entry | Next session open | Information-time boundary |
| Recipe B trigger window | 5 sessions, buy-stop one tick above the reaction high | Practice (episodic pivot) |
| Initial stop | Reaction-session low; position size cut if the stop is more than 3 ATR away | Practice; planner rule |
| Trend exit | Two closes below EMA20, active after 10 sessions | Existing component; practice (10/20-day trail) |
| Time limit | 40 sessions | Inference (inside the 60-day published window) |
| Exit before next report | Close of the session before it | Practice; gap risk |
| Risk per trade | 0.5% of account, shared pool | Practice (0.25–1%) |

## Appendix E: Components and look-ahead traps in full

| Component | State (from reading code on 4 Nov 2026) |
|---|---|
| ATR, EMA20, 50-day average volume, daily screen (price, dollar volume, history) | Exist (`DailyScreen`, `MinBreakoutVolumeVs50DayAverage`, indicator engine) |
| Two-close EMA20 exit, maximum hold in bars, ATR sizing, shared planner that never tightens stops | Exist (`ExitRules.RequireConfirmedEma20Exit`, `MaxHoldBars`, `SharedOrderRiskPlanner`) |
| Breakout trigger and stop at a candle | Partly: a `breakout_candle` stop mode exists; a level taken from an earlier event bar does not |
| Event study against an equal-weight eligible universe | Exists (`SignalStudyAnalyzer`; its default pass rules need multi-year samples) |
| Historical earnings event source for backtests (Finviz history with release time, first-seen time and raw payload) | **Missing.** Earnings data is used only by the live monitor (`src/TradingFlow.Earnings`) |
| Reaction-session resolver (release time to session, with an ambiguity check against bars) | **Missing** for backtests; the live analyzer has a partial version |
| Event-anchored levels (reaction high, low and close kept as fixed numbers for later bars) | **Missing** |
| Close-location filter | **Missing** (small) |
| Exit before the next scheduled report | **Missing** |
| One-shot event lifecycle (armed, triggered, used up or expired) | Not confirmed in code here; check against the catalyst lifecycle work |

**Look-ahead traps:**

1. **Revised surprise values.** A Finviz estimate or actual downloaded today may differ from what showed on the report day: forecasts get updated, adjusted and GAAP figures are swapped, results are restated. No documentation was found showing that Finviz keeps point-in-time snapshots. Use historical surprise as a diagnostic split only. Going forward, keep the raw payload and the time it was first seen; the live monitor already archives the JSON.
2. **Wrong release time.** Before-open and after-close labels are sometimes wrong or missing, and dates move. Resolve the reaction session from the label, the news time and a volume check. Drop disagreements as ambiguous and count them.
3. **As-traded versus adjusted prices.** Price and dollar-volume filters use as-traded prices. ATR and event levels use only splits known by that date, in the same price basis as the fills.
4. **Finviz's one-day price reaction field** is computed after the fact with its own reference price. Never use it as a signal.
5. **Same-bar fills.** No fill at the reaction-session close when that close created the signal.
6. **Gaps through stops** fill at the opening price, not the stop.
7. **Hindsight universe.** A current Finviz list projected backwards is diagnostic only (operating boundaries). Use the calendar's own historical events, with delisted names kept.
8. **After-hours prices** may feed a label but do not authorize an after-hours order.
