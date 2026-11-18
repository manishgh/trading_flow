# Registered experiment V2-01: median true range (MTR-14) instead of Wilder ATR(14) — 18 November 2026

Status: **registered before any result exists** (user: "only implement the right idea which you agree on", 18 Nov 2026).
Nothing is built yet: the median-true-range calculation is added when its first host strategy (below) is built.

## Why

A single shock bar inflates Wilder's ATR(14) for the next 14 sessions. Checked on real Alpaca data (INTC, 11 Nov 2026):
ATR(14) 6.02 against a median daily range of 3.28, after the −28.6 % earnings gap of 30 Oct. With a 1-unit stop and a
2.5-unit target, both distances roughly double, the target becomes harder to reach within the 10-day limit, and the
position shrinks because risk is fixed in R.

## Where it applies

- **Host strategy:** the 21-EMA trend pullback (Strategy B) and its short mirror, once designed and approved — they trade
  after shocks, so gap-inflated setups are common there.
- **Not the Bollinger squeeze.** The squeeze requires the 20-session band width in its narrowest 15 % of 60 sessions; a
  large gap inside the last 14 sessions widens the bands, so such setups rarely pass. The squeeze's first diagnostic run
  only **reports the share of its trades that are gap-inflated** (definition below); if it is small, no second squeeze
  trial is needed.

## Design (frozen)

- **Control:** the host strategy's frozen version, volatility unit = Wilder ATR(14) of the signal day.
- **Treatment V2-01:** volatility unit = median of the 14 true ranges ending on the signal day (true range counts the
  overnight gap: max(high − low, |high − previous close|, |low − previous close|)).
- **Identical otherwise:** entry signals, 14-bar window, stop 1.0 unit, target 2.5 units, 10-session time exit, and the
  **same account risk as the control** (0.5 % per trade, 0.25 % in the VIX 18–22 band — not 1.0 %). No parameter search.
- **Gap-inflated setup:** max(TR over the 14 bars) > 3 × MTR(14) on the signal day; all others are "normal".

## Data split (consistent with the project's data and the user's decisions)

- Development / walk-forward: 2017 – 2023 (whole-market daily data starts 2017; Alpaca history starts January 2016).
- Untouched final check: January 2024 – April 2026.
- May – October 2026: already spent by the user's 6-month diagnostic decision (18 Nov 2026); never counted as untouched.

## Report and decision

- Expectancy (R), profit factor, maximum drawdown, per calendar year, for all trades and separately for gap-inflated and
  normal setups; how many trades hit the 20 %-of-equity position cap in each version (a smaller unit means more shares).
- **Adopt V2-01** (it becomes the host strategy's frozen rule; this grants no paper or live permission) only if all hold:
  development expectancy at least +0.05 R better; in gap-inflated setups a higher target-hit rate with a stop-out rate no
  more than 5 percentage points higher; maximum drawdown no more than 1.0 percentage point worse; better in at least
  5 of the 7 development years; and **at least 30 trades in each group** — fewer trades in either group makes the
  result "inconclusive", not a pass.
- It is the second declared trial on the host strategy in the trial registry; the untouched period is read once, after
  the decision, for both versions.
- Same in both versions (existing engine rule): a daily stop is placed at the fill day's close.
