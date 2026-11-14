# TradingFlow audit: what you have, what is true, and what to change (14 November 2026)

Reviewer: Fable 5.2, acting as architect and senior trading engineer. Read-only audit of `C:\project\trading_flow`
(branch `main` at `568bc42`) and the data on `V:\trading_flow\data`. No code, configuration or data was changed, no
backtest or download was started, and the site was not restarted. The only file written is this one.

How to read the labels:

- **[Measured today]**: I recomputed it today from the published files or the operator database with a small read-only script.
- **[Read in code]**: I read the named file and line today.
- **[From the 12 November review]**: taken from `docs/research/fable-review-2026-11-12.md`; I did not re-open the outside sources today.
- **[Not verified]**: I did not check it. Treat it as unknown.

Where I am not sure, I say so and I do not make a recommendation from it.

---

## 0. The short, honest answers

1. **Why there is no profit.** No rule tested in this project has beaten simply holding the market. That is the measured
   result, not a software fault:
   - Buying dips in single stocks (Connors RSI(2), snapback, recovery): over 21,082 trades in 2017-2023 the gain was the
     market's own rise; the extra over holding the same stocks was +0.06% per trade, statistically zero.
   - Opening-range breakout (ORB), October 2026: every group with a result lost money. **[Measured today]** the assumed
     slippage was 18% to 54% of each group's loss; with slippage removed entirely every group still lost.
   - The two monthly fund rules lose return in exchange for smaller falls. Against SPY itself over the same years, the
     sector rotation earned 7.73% a year against SPY's 11.07% (both without dividends) **[Measured today]**.
2. **Are the backtests done the right way?** Yes, for the results published since 11 November. I recomputed the rotation
   from the raw bars: the same 57 purchases and 54 sales on the same days, every fill at the next session's opening
   price, the same return, worst fall and lowest cash. I found reporting defects (section 2.6) but none that changes a verdict.
3. **Is paper trading working as expected?** No. The operator database holds **zero** paper orders. The only paper
   experiment (12 November) never evaluated a stock: all 30 candidates were given a 3-minute lifetime and the first
   evaluation came 15 minutes later; after a restart every evaluation failed for two hours with a "different provenance"
   error. Also, no strategy in the catalog today is allowed to paper trade at all (section 5).
4. **Day trading.** It exists only as a backtest. There is no paper driver for same-day strategies, so nothing can be day
   traded on paper today. The published ORB recipe (the daily top-20 "stocks in play" selection) has never been run here;
   only its trade rules on 23 hand-picked stocks for one month.
5. **Live candles on every page.** No page draws a candle. The web project contains no chart library. The server already
   holds one Alpaca SIP websocket; the browser receives quotes and desk rows through server-sent events (a one-way push
   over HTTP), not a websocket (section 7).
6. **Finviz Elite** is used for screening, news and the earnings calendar, but only five fields of each screener row are
   kept, and screens are saved only when a run or preview happens to ask for them. Finviz cannot tell you what a screen
   contained on a past date, so nothing from Finviz can be backtested until you have stored it yourself day by day (section 8).
7. **What to do.** First fix the success definition, because its benchmark (SPY) is not the one the result documents
   used (an 11-sector basket). Then repair the paper path so it can prove operations. Then run one bounded day-trading
   study that settles ORB with data you mostly hold. Section 10 gives the order and the stop rules.

I cannot tell you a rule that will make money. Nothing in the evidence supports such a claim, and I will not guess one.

---

## 1. What I checked, and how

| Area | What I did | Label |
|---|---|---|
| Research documents | Read all nine in the brief's order, plus `docs/operating-boundaries.md` | Read |
| Code changes `fb3f6fc..568bc42` | Read the diff of the backtest runner, decision kernel, screen command, admission capacity and schedule files | Read in code |
| Rotation result (job `6e1e73fb`) | Recomputed every month-end decision from the frozen daily bars of the comparison run (job `d335fce2`, which holds all 11 funds from January 2016) and compared with the published trades; recomputed return, worst fall, invested share and lowest cash from the daily ledger | Measured today |
| Trend and buy-and-hold results (jobs `6bd8d1dd`, `86a63b21`, `d335fce2`, `6bd75d31`) | Recomputed return, worst fall, invested share and lowest cash from each daily ledger | Measured today |
| SPY over the same window | Computed from the frozen SPY bars in job `6bd8d1dd` | Measured today |
| Friday defect | Scanned the 271 published result files for daily runs on research lists before the fix; listed the fill sessions of the one affected run | Measured today |
| ORB October cost test (plan 105) | Totalled the 10 bps slippage inside the fill prices of each run | Measured today |
| Paper trading | Opened `V:\trading_flow\data\tradingflow.db` read-only; counted orders, candidates, runs and reconciliations; read the failure text | Measured today |
| Paper code path | Read `LiveRunner.ProcessTicker.cs`, `ProductionRunPersistence.cs`, `UniverseWorkflowAdapters.cs`, `TechnicalExecutionEngine.cs` | Read in code |
| Web pages | Listed every page and script; searched for chart libraries, candle drawing and stream endpoints | Read in code |

Not checked: the evidence catalog's Parquet files (I used the run-owned copies of the bars), the Android app, the Python
Market Predictor, the FinBERT sidecar, Alpaca's and Finviz's current terms, and any outside publication. I did not run the tests.

---

## 2. Audit of the verdicts (the brief's questions)

### 2.1 Do the verdicts follow the frozen rules?

Yes. **[Measured today]** from each run's daily ledger:

| Run | Job | Return per year | Worst fall (dates) | Share invested | Lowest cash | Document says |
|---|---|---|---|---|---|---|
| Rotation, 11 sector funds, top 3 | `6e1e73fb` | 7.73% | 22.20% (7 Jun 2022 to 17 Mar 2023) | 84.4% | $2,236.12 | 7.73%, 22.20%, 84%, $2,236 |
| Buy and hold, 11 sector funds | `d335fce2` | 8.30% | 34.59% (19 Feb to 23 Mar 2020) | 93.6% | $3,344.52 | 8.29%, 34.59%, 94% |
| Trend rule, 11 sector funds | `6bd75d31` | 4.74% | 13.44% | 62.8% | $3,053.71 | 4.74%, 13.44% |
| Trend rule, 15 funds | `6bd8d1dd` | 5.39% | 13.96% (4 Jan 2022 to 15 Mar 2023) | 65.3% | $3,067.49 | 5.38%, 13.96%, 65% |
| Buy and hold, 15 funds | `86a63b21` | 9.25% | 34.44% (19 Feb to 23 Mar 2020) | 94.9% | $2,706.86 | 9.24%, 34.44%, 95% |

The differences are rounding (8.30 against 8.29, 5.39 against 5.38). Applying the frozen rules to these numbers gives
the published verdicts: both fund rules are `RETAIN_RESEARCH`. I found no setting changed after a result was seen: the
strategy files and profiles in `git log` match the designs, and the one rerun (rotation, after job `457125c8` stopped on
negative cash) changed the cash reserve in admission code, not a strategy value, and is disclosed.

### 2.2 Fairness of the comparisons

- **Dividends and cash at 0%.** All versions use the same as-traded bars and the same engine, so the comparison is like
  for like. The documents state which way each omission leans. I agree with their reading: the omissions barely move the
  drawdown ratio, which decides the rotation verdict.
- **No monthly rebalancing.** Disclosed as a difference from the papers; it applies to every version.
- **The benchmark is not the one your success definition names.** See 2.6, finding F3. This is the one fairness point
  that matters for your decision.

### 2.3 Point-in-time correctness

- **12-1 ranking.** `DailyScreenCommand.Return12Minus1` (`src/TradingFlow.Cli/DailyScreenCommand.cs`) reads the 253 bars
  *before* the session it ranks for: the newer close is 21 sessions before the latest prior close and the older one 252
  sessions before it. Nothing from the ranked session itself is read. **[Read in code]**
- **Month-end from the calendar.** `DecisionSchedules.IsDecisionSession` decides from the exchange calendar's next trading
  session; a missing calendar date is an error, not a month boundary (`ExchangeSessionCalendar.NextTradingSession`). **[Read in code]**
- **Independent recomputation.** **[Measured today]** I rebuilt every month-end decision of 2017-2023 with my own code from
  the raw daily bars (top 3 by 12-1 return from closes up to the decision close; buy if the close is above its 210-session
  average; sell if off the top 3 or at or below the average). Result: 57 purchases and 54 sales, identical in date and fund
  to the published run, with XLK, XLC and XLY still held at the end. Every fill price equals the next session's open plus
  or minus 5 bps (first trade: XLB bought at 51.96597 = 1 February 2017 open 51.94 x 1.0005).

### 2.4 Same-open swaps and cash (`BacktestRunner.AdmissionCapacity.cs`)

- **Can a swap double-count?** No. Money available = closed-trade equity − planned cost of every held fund (including the
  ones being sold) + last-close value of the funds being sold − the 2.5% kept in cash. Each newly admitted purchase is
  added to the held list before the next candidate is measured (`BacktestRunner.Diagnostics.cs:653-662`), so the released
  value is spent once. **[Read in code]**
- **Can a swap overspend?** It can try to: the sold fund is counted at its last close but sells at the next open, and a
  held fund's cost is counted at its planning price, not its actual fill. The run then fails instead of publishing:
  `RequireSwapsKeptCashCovered` stops any run in which a session ends with negative cash. This is what stopped job
  `457125c8` (cash −$142.91 on 1 February 2018). In the published run the lowest cash was $2,236.12, so no session was
  overdrawn. **[Measured today]**
- **Gap left open** (finding F5): the negative-cash check runs only when same-open swaps are switched on.

### 2.5 The Friday defect

- **What it was.** Before commit `fb3f6fc`, a daily decision was checked against the research list for the UTC date of its
  decision time. A Friday close became Saturday, a day no list contains, so the entry was refused. The same happened
  before every holiday.
- **Which published results it touched. [Measured today]** Research lists exist since 11 November. Of the 271 published
  result files, 29 were started on or after that day: 20 ORB cost-test runs (same-day, wishlist), 3 Connors 9.2.0 checks
  (15:00 decisions), 5 ETF runs (after the fix), and one daily run on a research list from before the fix: job `9c5b5fb0`,
  `connors-complete-trial-201701`, the January 2017 engineering trial of Connors 9.1.0 (12 November). It shows the
  defect: its 35 closed trades were filled on 4, 6, 10, 11, 12, 13, 18, 19, 20, 25, 26 and 27 January. **None was filled
  on a Monday** (9, 23, 30 January) and none on the two Tuesdays after holidays (3 and 17 January).
- **Not affected:** the ten-year signal study (it checks membership on the entry session itself,
  `SignalStudyOutcomes.cs:104`); the Connors 9.2.0 checks (they decide at 15:00 on the same day); every ORB run (same-day);
  every wishlist run (no research list); all ETF runs (run after the fix).
- **Does any conclusion change?** No. The Connors retirement rests on the signal study, not on that trial, and the trial
  was never used for a verdict.

### 2.6 New findings from this audit

| # | Finding | Evidence | What it means for a decision |
|---|---|---|---|
| F1 | **Published trade times are one day late for daily fills.** A fund bought at the 1 February 2017 open is published with `EntryTimestamp` 2 February 05:00 UTC; the sale at the 3 April open is published as 4 April. The price and the daily ledger are right (the position is valued from 1 February). Only the label is wrong: it is the time the daily bar finished, not the time of the open. | `ChronologicalBacktestExecutionCoordinator.cs:993` (`tracker.FirstEntryFillUtc ?? candidate.EntryTimestamp`); job `6e1e73fb` trade 1 against the XLB bars | No verdict changes. Anyone reading the trade list sees the wrong day, and a paper comparison that matches trades by date would be off by one session. Fix before paper trading is compared with the backtest. |
| F2 | **The SPY figure inside every `result.json` covers the wrong period.** It runs from the first loaded warm-up bar (4 January 2016) to the end, not from the test start. Every ETF result shows SPY +136.45% and an "excess" of −68.2 points for the rotation. | `BacktestValidator.cs:110-116` uses the first and last of all loaded benchmark bars; 475.31 / 201.0192 − 1 = 136.45% | The research documents did not use this field, so no verdict changes. The field is wrong on the Backtests page for any run with warm-up, and must not be quoted. |
| F3 | **The success draft compares the rotation with the wrong benchmark.** Section 1 of the draft names SPY as the benchmark; section 3 then quotes "93% of buy-and-hold's return with 64% of its worst fall", which is against the 11-sector basket. Against SPY itself (bought at the same 1 February 2017 open, no dividends): SPY 11.07% a year with a worst fall of 34.18%. The rotation is **70%** of SPY's return with **65%** of its worst fall; the 15-fund trend rule is **49%** and **41%**. | Measured today from the SPY bars of job `6bd8d1dd` | Under draft option (b) (at least 80% of SPY's return with at most 60% of its fall) the rotation misses both lines, not one. Decide the benchmark before any further ETF test. |
| F4 | **The bias block of each result is stale.** The rotation's result says `UniverseSource: wishlist` and `PriceAdjustmentPolicy: alpaca_sip_cached`; the run used a research list and raw catalog bars. | `Validation.BiasRisk` in job `6e1e73fb` | Misleads a reader of the result file. No effect on numbers. |
| F5 | **Only swap runs are checked for negative cash.** The daily book reserves each position at its planning price, not its fill, and "has no cash check" (the code's own comment). | `BacktestRunner.AdmissionCapacity.cs:40-54` | I measured positive lowest cash in all five ETF runs. Earlier 10 x 10% Connors runs were never checked. Low risk, but an unchecked assumption. |
| F6 | **Exit reasons undercount "left the top 3".** When a fund is both off the list and below its average, the published reason is "below average". 7 of the 23 "below average" sales were also off the list. | Measured today | The document's "31 and 23" split is a labelling order, not two separate causes. |
| F7 | **Decision records are very large.** 806 decisions take 71 MB (about 88 KB each) because each embeds the fund's whole drawdown history. This filled the disk in the first trend attempt. | `result.candidate-decisions.json` in job `6e1e73fb` | Research on thousands of stocks will hit the disk again. |
| F8 | **`AGENTS.md` is out of date on the catalog.** It says 3 research artifacts; `configs/strategy-catalog.json` holds 6 (three ORB versions and three ETF versions). | Read today | Agents starting from `AGENTS.md` get the wrong picture. |

---

## 3. Strategy: what you have, how the system uses it, what is missing

### 3.1 Everything tested, and what the evidence says

| Idea | How it was tested | Result | Status |
|---|---|---|---|
| Connors RSI(2) dip buying, long | 21,082 trades, 2017-2023, point-in-time top 500 stocks, next-open entry, 10 bps per side | +0.33% per trade after costs; holding the same stocks earned +0.27%; edge +0.06%, t −0.42 | Retired 13 November |
| Same, ranks 501-1,500 | 15,653 trades | Edge −0.05% | Retired |
| Same, on 15 index and sector funds | 649 trades | Edge −0.09% | Retired |
| Connors short, snapback, recovery | Same idea; small wishlist tests only | Too few trades to judge; retired with the family | Retired |
| ORB 1.2.0, 2.0.0, 2.1.0 | 23 stocks from today's `volatile` wishlist, October 2026 only | Every group lost; see 3.2 | Research; not a verdict on the published recipe |
| Trend rule (hold a fund above its 210-session average) | 15 funds, 2017-2023 | 5.39% a year, worst fall 13.96% | `RETAIN_RESEARCH` |
| Sector rotation (top 3 by 12-1 return, with the trend rule) | 11 funds, 2017-2023 | 7.73% a year, worst fall 22.20% | `RETAIN_RESEARCH`; holdout 2024-2026 unopened |

### 3.2 ORB: what the October losses were made of **[Measured today]**

The 12 November review asked for this check (its step 4.1.6); I found no record that it was done, so I did it from the
published trade files of plan 105 (`orb-published-costs-volatile-oct-20261111`, 10 bps slippage on each fill, $10,000 per group):

| Group result (net) | Trades | Commission | Slippage inside the fills | Result with slippage removed |
|---|---|---|---|---|
| −$1,252 | 20 | $46 | $349 (28% of the loss) | −$903 |
| −$1,874 | 44 | $69 | $783 (42%) | −$1,091 |
| −$779 | 23 | $36 | $420 (54%) | −$359 |
| −$1,834 | 19 | $42 | $331 (18%) | −$1,504 |

(ORB 2.0.0, groups 2 to 5. ORB 2.1.0, same groups: losses of $1,207, $1,620, $815 and $1,749 with slippage of $349,
$795, $421 and $332; with slippage removed the results are −$857, −$825, −$393 and −$1,417. The slippage is estimated
as 10 bps of every fill price, the value set in both strategy files.)

What this settles: the overview's sentence "costs no longer explain the result" was incomplete, because slippage was a
quarter to a half of the loss; but it does not rescue the result, because every group still lost with no slippage at all.

What it does not settle: these are 23 stocks chosen with hindsight, one month, and 81 of 121 trades were scored as losses
by rule because entry and stop fell in the same five-minute candle. The paper's claimed edge comes from choosing each day
the 20 stocks with the highest opening volume against their own norm. **That selection has never been run here.**

### 3.3 How the system decides an entry and an exit today

- **Entry.** For each stock and completed bar, `StrategyDecisionKernel` checks the session window, the decision schedule,
  the strategy's indicator rules and the universe and market-regime evidence, then plans an order. Backtest and paper call
  the same kernel. Daily strategies fill at the next open; ORB rests a stop order from 09:35 New York.
- **Exit.** Stops, targets and time limits where the strategy has them; "technical" exits judged at a completed close and
  filled at the next open (close above the 5-day average, close at or below the 210-session average, fund off the list).
- **What the remaining strategies use.** ORB: the opening range and a stop 10% of the 14-day range away. ETF rules: one
  moving average and one ranking, once a month. No kept strategy uses news, earnings, support and resistance, or volume
  beyond ORB's opening volume.

### 3.4 What is missing, stated as facts

1. **No strategy with a measured edge.** This is the main gap, and software cannot close it.
2. **No strategy the paper side may run.** ORB is same-day and `LiveRunner.RequireSwingStrategies` refuses it; the ETF
   rules use a month-end schedule and `TechnicalExecutionEngine.RequireNoBacktestOnlyRules` refuses them. **[Read in code]**
3. **ORB's published selection is untested.** Opening five-minute bars exist for 241,891 stock-days (12 March to
   30 October 2026). Per the overview, the full-session five-minute bars (about 450 requests) and one-minute bars (about
   130 requests) were not collected, and ORB 2.2.0 (as-traded prices) was not built.
4. **No measured trading cost.** Every result uses an assumed 5 or 10 bps. No paper or live fill has ever been recorded
   (zero orders), so nothing has been measured.
5. **No dividend-inclusive benchmark.** The success draft asks for "SPY with dividends". The bars are as-traded and carry
   no dividends. I did not check whether the corporate-actions dataset `864016f2…` holds cash dividends **[Not verified]**.
6. **No history of Finviz screens.** See section 8.
7. **Support and resistance, or a trader's journal.** I found no journal or written rule set in the repository. Without a
   rule written down before the test, there is nothing to test honestly.
8. **Loss limits are still not enforced.** `max_daily_loss_pct` appears only in `ProductionParameterRegistry.cs:163`; no
   other code reads it. The same was found on 12 November. A daily cap on the number of new entries does exist
   (`OperationalTradingLimits`).
9. **The count of tries is still not recorded.** `docs/operating-boundaries.md` says saved-plan backtests are not in the
   trial registry. Without the count, no result can be corrected for luck.

---

## 4. Backtesting: is it done the right way?

**What is right (verified today):** decisions use completed bars; fills are at a later bar's open with slippage against the
trader; stock lists are computed from earlier sessions only; the calendar decides month-ends; runs are frozen and
reproducible; pass and fail rules are written before the run; a run that breaks an accounting assumption stops instead of
publishing (the negative-cash stop worked as designed).

**What is wrong or weak:**

- Reporting defects F1, F2, F4, F6 above. None changes a verdict; all can mislead a reader.
- **Seven years is short** for a monthly rule (84 decisions, one fast crash, one slow fall). The documents say so. No
  amount of engineering fixes this; only longer history does.
- **The holdout is single-use.** Choosing "top 5" after seeing top 3 and all 11 is tuning on the development years. It is
  allowed only as a new frozen hypothesis, and the 2024-2026 holdout can then be spent on one version only.
- **Order of work.** The cheap question "does this signal pay?" (the signal study, minutes) was answered for dip buying
  only after weeks of portfolio machinery. For ORB it has still not been asked on the published selection.

---

## 5. Paper trading: is it working as expected?

**No. [Measured today]** from `tradingflow.db` (records since the 4 November reset):

| Table | Rows | Meaning |
|---|---|---|
| `order_intents`, `order_events`, `position_events` | 0, 0, 0 | No paper order has ever been planned, sent or filled |
| `candidates` | 30, all `Expired` | The one experiment's stocks never reached a decision |
| `DecisionAudits` | 630 | 30 `candidate_expired`; 600 `ticker_pipeline_failed` |
| `Jobs` (paper) | 1, `cancelled` | `connors-9-0-0-paper-sp500-uptrend`, 12 November 19:27 UTC to 13 November 09:02 UTC |
| `runs` | 62, all `running`, none finished | One row per host start; never closed |
| `reconciliations` | 43,056, all `clean` | Broker and local records agree, because both are empty |

What happened in the one experiment, step by step:

1. **12 November 19:27:29 UTC:** 30 S&P 500 stocks discovered. Each candidate's lifetime was set to end at 19:30:29,
   three minutes later. The cause is a fixed three-minute source lifetime (`UniverseWorkflowAdapters.cs:21`), which
   `LiveRunner.ProcessTicker.cs:245-251` applies whenever it is earlier than the strategy's own one-bar lifetime.
2. **19:42:42 UTC:** the first evaluation ran, 15 minutes after discovery. All 30 candidates moved from `DataWarming`
   straight to `Expired`. A daily strategy that decides once a day was given three minutes to live.
3. **13 November 06:48 to 08:58 UTC:** every evaluation of every stock failed with "Run c35be1f9… already exists with
   different provenance". `ProductionRunPersistence.cs:21-29` refuses a run whose stored code version or configuration
   hash differs from the running program. The program had been rebuilt, so the running paper experiment could never
   continue; it failed 600 times until it was cancelled.

So the paper path has two defects that each stop it alone, and they were recorded as rejections rather than raised as a
failure. Separately, the strategy it ran was retired the next day.

**What the paper path cannot do today even if those are fixed:** run a month-end rule, run a same-day rule, or enforce a
loss limit. The 14 November design (`docs/design-review/2026-11-14/etf-rotation-paper-design.md`) covers the month-end case.

**What paper trading can and cannot prove** (the success draft has this right): a few months of paper trades cannot prove
profit. It can prove that orders match the backtest's decisions and that fills land near the modelled price. Today it
proves neither, because it has never placed an order.

---

## 6. Architecture: what to keep and the exact changes required

### 6.1 Keep (verified working today)

One decision kernel shared by backtest and paper; frozen, fingerprinted inputs; point-in-time research lists; the
exchange calendar from data; next-open fills; the stop-instead-of-publish checks; per-version monthly reporting; the
signal study as the first, cheap test of any idea.

### 6.2 Changes, in priority order

Each item needs your design approval before it is built (`AGENTS.md`). "Check" says how to know it is done.

**Group A: make the numbers on screen and in files true**

| # | Observed | Exact change | Where | Check |
|---|---|---|---|---|
| A1 | Daily fills are published with the bar-completion time, one day after the open that set the price (F1) | Stamp an open fill with that session's regular open from the exchange calendar (09:30 New York, or the half-day time); keep the "known at" time as a separate field | `ChronologicalBacktestExecutionCoordinator.cs` (fill event time and line 993); result writers | Rotation trade 1 reads 1 February 2017 13:30 or 14:30 UTC; ledger and prices unchanged; replay identity test still passes for prices and quantities |
| A2 | The SPY comparison spans the warm-up (F2) | Measure the benchmark from the first session of the evaluation window to its last, and publish it per month beside the strategy's months | `BacktestValidator.cs:100-128` | For job `6e1e73fb`'s inputs the field reads about +108% to +111% (window start to end), not +136.45% |
| A3 | The success draft needs SPY with dividends; bars have none | Add a total-return benchmark series built from as-traded bars plus cash dividends from the corporate-actions data; report strategy and benchmark on the same basis and say which | Evidence catalog reader; benchmark builder; monthly report | First confirm dataset `864016f2…` holds SPY's cash dividends; if not, one corporate-actions request for the benchmark and fund symbols |
| A4 | The bias block is copied from the profile, not from the run (F4) | Fill `UniverseSource`, the price basis and the list name from what the run actually used | Result builder that writes `Validation.BiasRisk` | Rotation result says research list `sector-funds-top3-…` and raw catalog bars |
| A5 | Negative cash is checked only with swaps on (F5) | Publish the lowest end-of-session cash for every daily book; fail the run when it is negative unless the profile declares borrowing | `BacktestRunner.AdmissionCapacity.cs:45-54` | Existing ETF runs report $2,236 to $3,345; a constructed overdraw fails |
| A6 | One exit reason is published when two hold (F6) | Record every exit condition that was true at that close | `BacktestRunner.Simulation.cs:249-254` | The seven sales list both reasons |
| A7 | 88 KB per decision record (F7) | Store each ticker's drawdown history once per run and reference it by hash from the decision | Candidate decision writer | The rotation's decision file falls from 71 MB to a few MB with the same hashes of content |

**Group B: make paper trading able to place and prove an order**

| # | Observed | Exact change | Where | Check |
|---|---|---|---|---|
| B1 | Candidates expire three minutes after discovery; the first evaluation came after 15 | A paper run's candidate lives until the strategy's own decision window ends (for a daily rule, the session it fills in). The three-minute lifetime stays for interactive previews only | `UniverseWorkflowAdapters.cs:21`; `LiveRunner.ProcessTicker.cs:236-251` | A test that starts a daily paper run and evaluates 15 minutes later reaches a decision, not `candidate_expired` |
| B2 | After a rebuild, a running paper run fails on every stock, every cycle, for good | On start, compare the stored and running code version once. If they differ, stop the run with one clear message and one alert, or continue only under an explicit recorded "continue under new build" decision | `ProductionRunPersistence.cs:21-29`; `SqliteOrderIntentRepository.cs:1608`; paper job start-up | A restart with a new build produces one stopped-run record, not 600 rejections |
| B3 | Failures were written as "Rejected" decisions | A pipeline error is a failure of the run: count it separately, show it on the Paper page, and stop new entries after N in a row | `LiveRunner` audit writing | The 13 November case shows as "run failing since 06:48", not as 600 rejected stocks |
| B4 | 62 `runs` rows are `running` and none finished | Close the row when the host stops or the next host starts | Run persistence at host start and stop | Only the live host's row is `running` |
| B5 | No catalog strategy may paper trade | Build the monthly rebalance path in the 14 November design (it reuses the backtest's ranking, schedule and capacity functions) | As that design lists | Its comparison test: every 2017-2023 month-end paper decision equals the backtest's |
| B6 | No fill has ever been compared with the model | For each paper order store the decision price, the quote when sent, and the fill; report the difference by strategy and month | Order journal; Paper page | After the first rebalance, the page shows modelled open against actual fill for each order |
| B7 | Loss limits are declared, not enforced | Enforce the daily, weekly and peak-to-trough loss limits in the order path; a breach stops new entries and is recorded in `risk_events` | `EntryGateChain.cs`; `ProductionParameterRegistry.cs:163` | A test that books losses past the limit sees the next entry refused |

**Group C: research flow**

| # | Observed | Exact change | Check |
|---|---|---|---|
| C1 | Tries are not counted | Register every saved-plan backtest and signal study in the trial registry, by idea family | Each result shows "try N of this family" |
| C2 | Each new idea was built as a portfolio run first | Rule: an idea gets a signal study on the frozen data before any strategy version, profile or portfolio run exists | The next idea's first artifact is a study manifest |
| C3 | The success definition has no numbers | You fill in sections 1 to 4 and 6 of the draft, with SPY (same basis as the strategy) as the stated benchmark | A signed, dated version of the page |
| C4 | `AGENTS.md` describes a 3-artifact catalog and old site port | Correct the "Active Configs" paragraph | Matches `configs/strategy-catalog.json` |

**Group D: live candles** is in section 7, because it is mostly a screen change on top of one small server piece.

---

## 7. Screens: order, content and new components

### 7.1 What exists **[Read in code]**

- **Pages:** Desk, Strategy runs (Paper, PaperJob), Backtests (Job, OptimizationJob, Audit, CandidateAudit), News, Earnings,
  Orders, RunningTrades, Wishlists, Warmup, Operations, Preparation, OrderTicket, Users.
- **Live data in the browser:** server-sent events only. `/api/desk/stream` sends desk snapshots and changed quotes;
  `/api/v1/desk/quotes/stream` feeds the Orders page; candidate-run events feed the Desk workflow.
- **Candles:** none. No page draws one. There is no chart library under `wwwroot`, and no script loads one. PaperJob shows
  a table of the latest completed candle's numbers.
- **Alpaca:** the server owns one SIP websocket under the lease `alpaca:paper:sip` (the lease row was live when I looked).

So your requirement "every page has websocket Alpaca candles" is not met on any page today.

### 7.2 The one server piece needed (D1)

A **candle hub**: one endpoint that a page subscribes to with a list of symbols and a timeframe. It sends the completed
bars the server already holds for those symbols, then pushes each new completed bar and the still-forming bar as trades
arrive. It reads the existing in-memory market state; it opens no second Alpaca connection.

- **Transport.** Your requirement says websocket, so the hub is a websocket (ASP.NET Core SignalR or a raw websocket).
  Browsers must not connect to Alpaca directly: the API keys would reach the browser, and Alpaca limits how many stream
  connections one account may hold **[Not verified today; the single leased connection in the code is consistent with it]**.
- **Honesty rule on screen.** Each chart states its feed (SIP), the age of its last bar, and "stale" when the server stream
  is behind. A forming bar is drawn differently from a completed one, because strategies decide on completed bars only.
- **Symbols outside the stream.** A page asking for a symbol the stream does not carry gets stored bars plus a clear
  "not streaming" mark, never a silent flat line.

### 7.3 The one chart component needed (D2)

A single reusable chart, used everywhere: candles and volume, with optional layers supplied by the page:

- strategy levels: opening range high and low, entry, stop, target; the 210-session average for the fund rules;
- markers: decision time, fill, exit, each with its reason;
- events: news and earnings times;
- session shading for pre-market, regular and post-market in New York time.

It needs a chart library; none is in the project. Choosing one is a dependency decision for you to approve. I am not
naming one as "best" because I have not compared them against this code today.

### 7.4 Page order and what each page gains

The order follows one question at a time, left to right in the navigation:

| Order | Page | The question it answers | Change |
|---|---|---|---|
| 1 | **Desk** | What is the market doing in my lists right now, and is anything close to a signal? | Add a small live candle panel for the selected row; a first row showing the benchmark (SPY) and the rule's own state (for the rotation: this month's top 3 and each fund's distance from its average) |
| 2 | **Strategy runs** (Paper) | Is the automated run healthy, and did it do what the backtest would have done? | New top strip: run state, last decision time, next decision time, failures since start (B3). New table: each order's modelled price against its fill (B6). Chart per position with entry and exit markers |
| 3 | **Orders and positions** (merge RunningTrades into it) | What do I hold, and what is working at the broker? | Live candle with the position's entry, stop and current value; one place instead of two pages |
| 4 | **News** and **Earnings** | What happened to a company, and how did price react? | A candle panel beside the selected item with the news or release time marked |
| 5 | **Backtests** | Did the idea pass its frozen rules? | New first card per result: the frozen pass and fail lines, the measured values, the verdict, and the benchmark on the same basis (A2, A3). Monthly table by version (already the convention). Trade list opens the chart at that trade with the corrected fill time (A1) |
| 6 | **Research ledger** (new page) | What has been tried, what failed, how many tries per idea, what is unopened? | Built from the trial registry (C1), the retirement ledgers and the success definition. It replaces reading eight documents to learn the state |
| 7 | **Operations**, Warmup, Preparation, Wishlists, Users | Is the machinery healthy? | Unchanged, moved to the end |

The Research ledger is the one new page I am confident is worth building: the current state of this project is only
discoverable by reading the documents in the right order, which is what this audit had to do.

---

## 8. Finviz Elite and Alpaca: how they are used, and how to get more from them

### 8.1 Finviz today **[Read in code]**

- **Screener export** (`FinvizClient.cs`, `/export?…&auth=`): each row keeps five fields only: ticker, relative volume,
  company, sector, market capitalisation (`FinvizScreenerRow.cs`). Every other exported column is dropped.
- **News** (`/export/news`, `/news?t=`) and the **earnings calendar** are collected (11,303 news items, 7,508 calendar
  events in the database).
- **Saved screens:** two, both mean-reversion screens ("Average pullback", "snapback"). Mean reversion was retired on
  13 November, so the saved screens no longer match any kept strategy.
- **Stored history of screens:** a handful of snapshots, taken only when a preview or run asked (for example 14 snapshots
  of one ticker list in late October, 2 of the S&P 500 uptrend screen on 12 November).

### 8.2 The limit you must know

Finviz shows what a screen contains **now**. It cannot show what it contained on a past date. Running a strategy over
the past on today's screen result uses knowledge of the future; `docs/operating-boundaries.md` already forbids reading
such a result as evidence. So Finviz cannot "maximise profitable setups" in a backtest. It can do two honest things:

1. **Feed today's candidates** to a strategy whose rule was proven on point-in-time lists built from price data.
2. **Become its own history**, if you store it every day from now on.

### 8.3 Exact changes

| # | Change | Why (evidence) |
|---|---|---|
| E1 | Archive each saved Finviz screen on a fixed schedule (for example 09:00 and 16:15 New York every session), with **all** exported columns and the receive time, as immutable snapshots | Today only five fields and occasional snapshots are kept; after some months you hold a real point-in-time record of each screen that can be tested |
| E2 | Keep Finviz relative volume as discovery information only; strategies keep using volume measured from Alpaca bars | Already the rule in `AGENTS.md`; no change, restated so E1 is not misread |
| E3 | Replace the two mean-reversion screens with screens that match what is being studied (for ORB: price, average volume and range floors, so the list is the paper's candidate pool) | The saved screens serve a retired family |
| E4 | For each screen, record forward results: what the listed stocks did over the next 1, 5 and 20 sessions against SPY | This is the only way to learn whether a Finviz screen has value; nothing in the data today answers it |

I am not claiming any Finviz pattern or signal filter is profitable. There is no evidence in this project either way.

### 8.4 Alpaca today

- **Broker:** paper account, guarded by the expected account id. No order has been sent.
- **Market data:** SIP history through the candle pipeline and one SIP websocket for live state.
- **News:** REST and a news stream client exist (`AlpacaNewsStreamClient.cs`).
- **Known limits to plan around [From the 12 November review]:** the paper account fills at the quoted price without regard
  to available size, and does not simulate dividends. So paper fills on thinly traded stocks at the open are not
  evidence of real fills; paper fills on the largest funds are a fair check.

---

## 9. The strategies written as specifications, with gaps

### 9.1 `etf.rotation-12-1@1.0.0` (monthly, sector funds)

| Item | Specification as built |
|---|---|
| Funds | XLK, XLF, XLE, XLV, XLY, XLP, XLI, XLB, XLU, XLRE, XLC |
| Decision time | Close of each month's last session (exchange calendar) |
| Ranking | 12-1 return from closes up to the decision close; top 3 |
| Entry | Buy at the next open a top-3 fund whose close is above its 210-session average and that is not held |
| Exit | Sell at the next open a held fund that left the top 3 or closed at or below its average |
| Size | Marked equity divided by 3, capped at 32.5%, 2.5% kept in cash |
| Costs | 5 bps per side, SEC and FINRA sell fees |
| Development result | 7.73% a year, worst fall 22.20%, 57 purchases in 7 years |
| Against its frozen rules | Return line passed (0.93 of the basket); fall line not passed (0.64, needs about 0.5) |
| Against SPY, same basis | 0.70 of the return, 0.65 of the fall |

Gaps: (1) not runnable on paper (B5); (2) holdout unopened and single-use; (3) benchmark basis undecided (C3, A3);
(4) seven years only; (5) no trimming back to equal weight, disclosed.

### 9.2 `etf.trend-10m@1.0.0` (monthly, 15 funds)

Same engine and timing without the ranking; 15 slots of 6.5%. Development: 5.39% a year, worst fall 13.96%. Fall line
passed (0.41), return line not passed (0.58, needs 0.70). Against SPY: 0.49 of the return, 0.41 of the fall. Gaps as 9.1.

### 9.3 `technical.opening-range-breakout@2.1.0` (same-day)

| Item | Specification as built |
|---|---|
| Range | First five-minute candle of the regular session |
| Direction | The direction of that candle's body |
| Entry | Stop order at the range edge from 09:35 New York; one entry per stock per day |
| Stop | 10% of the 14-day average range, measured from the fill |
| Exit | Stop, or the 16:00 close |
| Costs in the October test | $0.0035 a share, 10 bps slippage per fill, up to 4x gross |
| Result | Every October group lost; with slippage removed every group still lost (3.2) |

Gaps: (1) the daily top-20 selection, which the paper credits for its result, is not applied; (2) the as-traded version
2.2.0 is not built; (3) full-session five-minute and one-minute bars for the selected stocks are not collected; (4) two
thirds of trades cannot be ordered inside one candle; (5) it trades both directions, and the borrow cost and
availability of its short sales are not modelled; (6) no paper driver; (7) the real cost of a stop order at 09:35 is unmeasured.

### 9.4 Ideas named but with nothing built or tested here

- **ORB plus support and resistance from a trader's journal.** No journal or written rules exist in the repository. The
  first step is the written rule, dated before any test. Until then there is nothing to specify.
- **Top-5 rotation.** Needs no new code: a new ranked list (`--rank-tier 1-5`) and a 5-slot profile. It is a new
  hypothesis chosen after seeing top 3 and all 11, so it needs its own frozen design, and only one rotation version may
  ever open the holdout.
- **Earnings reaction, SPY intraday momentum** (the 12 November review's options D and E). Not started. I have no new
  evidence on either and make no recommendation beyond that review's own caveats.

---

## 10. What to do next, in order

1. **Decide the success definition (your decision, no code).** Name SPY on the same basis as the strategy as the
   benchmark, and fill in capital, acceptable fall and the return option. With today's numbers: option (a) "beat SPY" is
   met by nothing tested; option (b) as drafted is met by nothing tested (rotation 0.70 and 0.65 against lines of 0.80 and
   0.60); option (c) was not measured for any version. If (b) or (a) is your choice, the honest alternative that the
   evidence supports is holding the index fund itself, and automated trading remains a research project, not an income.
2. **Fix what makes numbers untrue: A1, A2, A4** (small, no behaviour change), then A3 if the benchmark with dividends is chosen.
3. **Repair the paper path: B1 to B4**, then build B5 (the approved-for-proposal rotation paper design) and B6. Purpose:
   prove the system can place the orders its backtest would place and measure real fills on the most liquid funds. This
   is worth doing even though the rotation did not pass, **only if** you want the machinery proven; it is not a profit test.
4. **One bounded day-trading study, then a rule decides.** The per-trade ORB study the 12 November review specified: the
   daily top-20 list by opening volume, as-traded prices, results at 0, 5 and 10 bps per side, no portfolio. It needs
   ORB 2.2.0's price handling, about 450 + 130 data requests, and the signal-study pattern, not the portfolio runner.
   Freeze the rule first: continue only if the average result at 5 bps is clearly above zero; otherwise retire ORB.
   I do not know which way it will go. The evidence so far (October, and the user reports cited on 12 November) leans against.
5. **Before real money, in any case: B7** (loss limits) and the rest of section 2.3 of the 12 November review.
6. **Start E1 now** (archive Finviz screens daily). It costs little and time is the only way to get that history.
7. **Do not** add strategy versions, filters or indicators to rescue a failed idea, and do not test on wishlists.

**Stop rules** (from your own draft, section 7): a failed frozen test retires the idea; a new version needs a new written
reason frozen before its test; more data is bought only when a pending decision depends on it.

---

## 11. What I did not verify, so you know the edges of this audit

- Whether Alpaca's inactive-stock coverage leaves a survivor bias in the point-in-time stock lists (the 12 November review
  says about two thirds of delisted companies are missing; I did not re-measure it).
- Whether the corporate-actions dataset contains cash dividends.
- The frozen catalog's Parquet files directly; I read each run's own copy of its bars.
- That the test suite passes on `568bc42`; I ran no tests.
- Any outside paper, vendor price, or Alpaca and Finviz term; nothing was looked up on the web today.
- The Android app, the Market Predictor project and the FinBERT sidecar.
- Whether a flatten-all (close every position) path exists; I saw `CancelAllOrdersAsync` on the broker client and an empty
  `kill_switch_events` table, and looked no further.
