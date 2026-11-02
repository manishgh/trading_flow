# Brief for an independent research and verification reviewer

Use this brief with a separate model session (for example Fable). The reviewer reads and checks;
it does not change code or data. Report findings with file:line evidence, separate what was
verified from what is inferred, and use plain language.

## Context

TradingFlow is a C#/.NET research and paper-trading system at `C:\project\trading_flow_swing`
(branch `approved-execution-and-signal-study`). Strategies must be C# recipes built from reusable
TradingFlow components, run through one decision path for backtest, paper and live. Machine
learning lives in a separate project and is advisory only. Rules: `AGENTS.md`,
`docs/operating-boundaries.md`, `docs/research/non-ml-strategy-research-program.md`.

After 200+ unsuccessful strategy iterations, the diagnosis was: hand-picked stock lists, one
six-month rising-market window, look-ahead in screening, and results without statistical tests.

## Part 1: research (can start now)

Day trading:

1. Opening-range breakout on stocks in play (Zarattini, Barbon and Aziz 2024, SSRN 4729284).
   Check the rules recorded in `docs/design-review/2026-11-02/orb-stocks-in-play-design.md`
   against the paper. Find independent replications on US single stocks and any evidence of
   decay after publication. What costs, slippage on stop orders, short-sale limits and data
   choices change the result most?
2. Intraday momentum "noise area" on SPY (Zarattini, Aziz and Barbon 2024, Swiss Finance
   Institute paper 24-97). Same questions.

Swing:

3. Short-term reversal and Connors RSI(2) on liquid large caps. Is there evidence it survives
   costs after 2011? Which exit and stop choices matter? Our own check found a 2xATR stop removed
   most of the edge on 75 large caps; is that consistent with the literature?
4. Cross-sectional momentum (12-month return skipping the last month, monthly rebalance), with
   volatility scaling against crashes. Current evidence and realistic costs.

For each: the best evidence for and against, what must be true of the data (delisted stocks,
as-traded prices, point-in-time membership), and pre-registered pass and fail rules you would set.

## Part 2: verification (after results exist)

1. Read `src/TradingFlow.Research/Signals/SignalStudyAnalyzer.cs` and its tests. Are the
   statistics and decision rules sound?
2. Read the opening-gap change (`git log` commit "Keep opening gaps through the stop") and say
   whether planning from the prior close and exiting at the next open is unbiased.
3. When ORB results exist: the planned 2016-2023 replication against the paper's gross figures
   no longer applies: the user chose a six-month window (1 May to 30 October 2026), so judge that
   window against the frozen pass rules in the design document and say what six months cannot show.

## What not to do

Do not edit files, run backtests, place orders, or treat any result as proof without checking
the code and data behind it.
