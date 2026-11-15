# What success means for TradingFlow: draft for the user to complete (14 November 2026)

Status: draft. The numbers marked **to confirm** are proposals; the user sets them. Once confirmed, every new strategy
test, paper run and real-money decision is judged against this page, so each test ends in a clear yes or no.

Factual corrections: 15 November 2026, approved NEXT-1 documentation scope. This edit does not adopt the proposed
investment targets or authorize a study, paper order, rotation strategy or live trading. The current paper controls
use 2% daily, 4% weekly and 10% observed-peak entry stops; the draft risk numbers below do not replace them.

## Why this page exists

**What was tested:**
- Single-stock swing rules: Connors RSI(2) long and short, snapback, recovery.
- Same-day opening-range breakout: ORB 1.2.0, 2.0.0 and 2.1.0.
- Two monthly fund rules: the 10-month trend rule and the sector rotation.

**What the tests found and what their stock lists establish:**
- **Single-stock reversal rules:** the published 2017–2023 signal study used point-in-time eligible lists and did not
  establish a reliable advantage over holding the comparison stocks.
- **ORB:** the October test used a fixed, selected stock list; it did not apply the published daily top-20 selection.
  Every reported October group lost money, including the audit's zero-slippage comparison. This is diagnostic evidence,
  not proof that the paper's complete point-in-time method has been tested.
- **Fund rules:** their published price-only tests trade return for smaller falls. These fixed fund-universe comparisons
  are distinct from a historical stock-screen membership claim. Neither strategy nor benchmark includes cash dividends.

**What was missing:** a definition of what result would be good enough. Without one, research keeps going and data
keeps growing without a decision.

## 1. The money

- **Capital for automated trading:** **to confirm** (for example $10,000 to $25,000 to start).
- **Proposed benchmark, to confirm:** simply holding SPY over the same months, with dividends. Comparable strategy
  dividend accounting and verified corporate-action coverage are required before claiming that total-return comparison.
  Current published comparisons are price-only and must remain labelled that way. The user still needs to select the
  return/risk objective below; benchmark choice is not implied by approval of software repairs.

## 2. Acceptable risk

- **Largest fall of the account from its peak** you would accept without stopping: **to confirm** (proposal: 20%).
- **Largest loss in one month:** **to confirm** (proposal: 8%).
- **Stop rule:** if the account falls past the largest-fall limit, automated trading stops and nothing restarts without
  a review.

## 3. Return wanted

**Choose one, to confirm:**
- **(a) Beat SPY:** a higher return than holding SPY, at similar or lower falls.
- **(b) Match SPY with smaller falls:** at least 80% of SPY's return with at most 60% of its worst fall.
- **(c) Steady income:** a positive return in at least 9 of 12 months, whatever SPY does.

**The rotation's audited comparisons:** the approximately 93% return ratio and 64% worst-fall ratio refer to the
11-sector buy-and-hold basket, not SPY. Against price-only SPY over the matched evaluation window, Fable measured
approximately 70% of its annualized return and 65% of its worst fall. Against the proposed option (b)'s 80%/60% lines,
that misses both thresholds. These are descriptions of existing research, not adopted success rules. Rotation is
disabled and deferred. See the [Fable audit, F3](tradingflow-audit-2026-11-14.md#26-new-findings-from-this-audit).

## 4. How often to trade

**Choose, to confirm:**
- **Monthly (about 12 decisions a year):** the only rules with long published records (the fund rules).
- **Daily swing (decisions each evening, held days to weeks):** no rule tested so far beat holding the stocks.
- **Same day (in and out within the session):** no rule tested so far beat holding the stocks, and it needs a paper
  driver that does not exist yet.

## 5. What paper trading must prove

Paper trading lasts weeks or months, far too few trades to prove profit. It must prove that the live system does what the
backtest did:
- **Orders:** every order is the one the backtest rules would place.
- **Fills:** fills land within a stated distance of the modelled price: **to confirm** (proposal: within 0.10% on average for funds, 0.30% for single stocks).
- **No silent gaps:** no missed decisions, no duplicate orders, no unexplained trade.
- **Length:** at least 3 months for daily rules, and 6 monthly decisions for monthly rules.

## 6. Real money

Real money only when **all** of these hold:
1. **Backtest:** the strategy passed its frozen development test and its untouched holdout test.
2. **Paper:** paper trading met section 5.
3. **Fit:** the strategy fits sections 1-4.
4. **Approval:** the user approves the exact strategy version in writing.

**Starting size:** at most **to confirm** (proposal: 25%) of the capital in section 1 for the first 3 months.

## 7. What stops research on an idea

- **Failed frozen test:** the idea is retired, as Connors, snapback and recovery were. No retuning on the same data.
- **New version:** it needs a new written reason (a published method, or a written-down rule from a real trader's record), frozen before its test.
- **More data:** bought only when a pending decision depends on it.
