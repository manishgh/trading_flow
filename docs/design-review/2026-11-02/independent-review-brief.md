# Brief for an independent research and validation reviewer

Use this brief in a separate model session (for example Fable). The reviewer researches, designs
on paper and checks; it does not edit code, change data, run backtests or place orders. Report
with file:line or document evidence, mark each finding as verified or inferred, and use plain
language (explain any technical term where it first appears).

## 1. Context

- Repository: `C:\project\trading_flow_swing`, branch `approved-execution-and-signal-study`;
  GitHub `main` is at commit `67da84c`. C#/.NET research and paper-trading system.
- Binding rules: `AGENTS.md`, `docs/operating-boundaries.md`,
  `docs/research/non-ml-strategy-research-program.md`.
- Strategies must be C# recipes (name plus parameters) built from reusable TradingFlow
  components, run through one decision path for backtest, paper and live. No Python in
  TradingFlow except the FinBERT sentiment service. Machine learning lives in the separate
  Market Predictor project and is advisory only in the UI.
- Why this work exists: after 200+ unsuccessful iterations the diagnosis was hand-picked stock
  lists, one six-month rising-market window, look-ahead in screening (split-adjusted prices),
  added stop and target overlays that removed the edge, and results without statistical tests.

## 2. Data windows (user decision, 2 November 2026)

| Use | Bars | Window |
|---|---|---|
| Swing setup | Daily | 1 year of decisions (Nov 2025 - Oct 2026); daily bars stored from 1 Sep 2024 for warm-up |
| Swing entry timing | 1 hour | Same year, only for selected stocks (not yet collected) |
| Day trading | 5 minute | 6 months (1 May - 30 Oct 2026) plus 14 earlier sessions for baselines |

Already collected (evidence catalog `C:\project\trading_flow\data\research\evidence\catalog.db`):

- Security master as of 30 Oct 2026: 33,556 Alpaca US equities, active and inactive
  (dataset `6720a7d9...`), and 387,945 corporate actions 2016-2026 (`864016f2...`). Alpaca's
  inactive list missed 39 of 60 known delisted companies when tested on 31 Oct 2026.
- Daily as-traded SIP bars, 1 Sep 2024 - 30 Oct 2026: 5,841,142 bars, 15,189 listed equities
  (`5fdf1ee3...`). Excluded and recorded: 242 symbols held by two companies, 431 non-ticker names.
- Daily stocks-in-play screen 1 Apr - 30 Oct 2026:
  `C:\project\trading_flow\data\research\screens\orb-stocks-in-play-20260401-20261030`.
  On 30 Oct it gave 1,259 stocks against 1,270 from the equivalent Finviz Elite screen, 1,166 in
  both. Funds excluded with a dated Finviz fund list (`finviz-funds-20261102.txt`, 5,802 funds).
- Dated S&P 500 membership from May 2018 exists in Market Predictor
  (`C:\project\market-predictor\data\canonical\index_membership`); importing it into TradingFlow
  is approved but not built.

## 3. Choose the strategies: two for day trading, two for swing

The main task is to find the best two day-trading and two swing strategies for this system,
not to polish ours. Section 4 lists the four we have looked at so far. Treat them as
candidates to challenge, not as the answer: keep, replace or reject each one with reasons,
and do not prefer them because some code already exists.

Limits every candidate must respect (from `docs/operating-boundaries.md`):

- US-listed stocks and exchange-traded funds; long or short; no options, futures, crypto or
  high-speed trading; regular session unless the source needs extended hours (say so).
- Rules fixed in advance, with no machine learning. Point-in-time news and earnings dates may
  be used where the source uses them.
- Data from Alpaca (consolidated SIP bars, trades, quotes, news, corporate actions); Finviz only
  for today's screening, never as past membership.
- The data windows in section 2. If a strategy cannot be tested honestly in those windows, say
  what extra history it needs instead of dropping it silently.

How to choose:

1. Build a long list of five to eight published candidates per category, each with its source,
   one-line rule and the reason it is kept or dropped.
2. Rank on: evidence that survives costs in independent replications and after publication;
   enough trades in our window to judge it; simple rules with few settings; data we can obtain
   without look-ahead; and capacity at our size. Prefer a pair whose returns do not depend on
   the same market conditions (for example one trend-following and one mean-reverting).
3. Pick two per category. For each pick: (a) check the published rules against the primary
   source and list every detail that is easy to get wrong; (b) summarise the best evidence for
   and against, including decay after publication and cost sensitivity; (c) say what our data
   window can and cannot show; (d) write the recipe as building blocks (stock selection, levels
   or bands, filters, trigger, entry order, stop, target, exit, sizing) and name which
   TradingFlow components exist and which are missing; (e) propose pass and fail rules to freeze
   before results are read, sized to the window; (f) list the look-ahead and point-in-time traps
   specific to it.

## 4. Our current shortlist (to challenge, not to adopt)

### Day trading: opening-range breakout on stocks in play

- Source: Zarattini, Barbon and Aziz (2024), SSRN 4729284.
- Our design: `docs/design-review/2026-11-02/orb-stocks-in-play-design.md` (rules checked against
  the paper, frozen choices where it is silent, cost model, pass rules for six months).
- Existing code: `src/TradingFlow.Engine/Strategies/OpeningRangeBreakoutEvaluator.cs`
  (15-minute range, fixed to earlier approved baselines), `src/TradingFlow.Engine/Universe/DailyScreen.cs`.
- Our earlier ORB on 8 fixed stocks: -0.36R per trade net, +0.14R gross, costs 0.50R because
  stops were about 0.6% from entry. The paper reports Sharpe 0.48 without stock selection.

### Day trading: intraday momentum "noise area" on SPY

- Source: Zarattini, Aziz and Barbon (2024), "Beat the Market: An Effective Intraday Momentum
  Strategy for S&P500 ETF (SPY)", Swiss Finance Institute paper 24-97.
- Questions: the exact band formula, decision times, VWAP trailing stop, sizing and costs; whether
  five-minute bars and six months are enough when the paper uses finer bars over 2007-2024; what
  a single ETF needs (no stock selection) and which components are missing (band from average
  intraday move by time of day, VWAP-based trailing exit).

### Swing: short-term reversal / Connors RSI(2)

- Sources: Connors and Alvarez (2009); academic short-term reversal (Jegadeesh 1990, Lehmann
  1990; de Groot, Huij and Zhou 2012; Medhat and Schmeling 2022 on costs and recent decay).
- Our state: `configs/backtest/strategies/swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml`
  (version 7.0.0 keeps opening gaps instead of deleting them; still carries a 2xATR stop, 2.5R
  target and five-session limit). On 75 current large caps, April 2020 - July 2026, the published
  rules gave +0.37% per trade (date-clustered t 2.38, 1.77 in a stricter one-position run), while
  the project's overlays gave +0.19% (t 0.34). Those numbers come from a survivor list.
- Questions: published exits and whether any stop is defensible; which universe (dated S&P 500
  versus a liquidity screen); whether one year of decisions can give enough trades; how the
  one-hour entry bars should be used without look-ahead.

### Swing: cross-sectional momentum

- Sources: Jegadeesh and Titman (1993); Daniel and Moskowitz on crashes; volatility scaling
  (Barroso and Santa-Clara 2015).
- Existing code: `src/TradingFlow.Research/Momentum/CrossSectionalMomentumResearchAnalyzer.cs`,
  `src/TradingFlow.Research/Universe/PointInTimeUniverseBuilder.cs`. An earlier diagnostic on 75
  current large caps was positive only in one year and its holdout is already used.
- Questions: with daily bars from Sep 2024 the 12-month look-back allows about one year of
  monthly decisions, which is about 12 rebalances. Is that enough to say anything? What is the
  honest minimum, and what can a one-year test still rule out?

## 5. Validate our shared setup

Check each against the code and report defects with a concrete failing case:

1. Point-in-time selection: `src/TradingFlow.Engine/Universe/DailyScreen.cs`
   (`SplitBasis`, price on the session open, 14 prior sessions) and
   `src/TradingFlow.Cli/DailyScreenCommand.cs`. Any use of later data? Split handling right?
2. Data collection: `src/TradingFlow.Cli/IdentityEvidenceCollectionCommand.cs`,
   `src/TradingFlow.Cli/DailyBarsCollectionPlanCommand.cs`. Are the exclusions (reused symbols,
   non-ticker names, over-the-counter, funds) reasonable and fully disclosed? What survivorship
   bias remains and how should it be bounded?
3. Signal study: `src/TradingFlow.Research/Signals/SignalStudyAnalyzer.cs` and
   `src/TradingFlow.Tests/SignalStudyAnalyzerTests.cs`. Are the statistics (Newey-West daily
   series with zero-filled days, 20-day block bootstrap, unviewed-data test, frozen worst case
   for missing outcomes, market sensitivity) and decision rules sound?
4. Execution: the opening-gap policy (commit "Keep opening gaps through the stop"), stops being
   active only from the next bar on daily paths, and the code-review list of gaps in
   `orb-stocks-in-play-design.md` (fixed 3R target, stop from signal not fill, no per-share
   commission, exposure cap at 100%, exit five minutes before the close).
5. One decision path: does anything in research or backtest decide differently from paper/live?

## 6. Output wanted

One report with:

1. The long lists from section 3, one table per category.
2. The two chosen day-trading and two chosen swing strategies, each with (a)-(f).
3. For each strategy in section 4, whether it was kept, replaced or rejected, and why.
4. Which one to build first and why.
5. The missing reusable components across the four chosen strategies, without duplicates.
6. The setup defects from section 5, ordered by how much they would mislead a result.

Keep it under 3,500 words; put long evidence in an appendix.
