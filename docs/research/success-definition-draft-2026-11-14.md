# What success means for TradingFlow: draft for the user to complete (14 November 2026)

Status: draft. The numbers marked **to confirm** are proposals; the user sets them. Once confirmed, every new strategy
test, paper run and real-money decision is judged against this page, so each test ends in a clear yes or no.

## Why this page exists

**What was tested:**
- Single-stock swing rules: Connors RSI(2) long and short, snapback, recovery.
- Same-day opening-range breakout: ORB 1.2.0, 2.0.0 and 2.1.0.
- Two monthly fund rules: the 10-month trend rule and the sector rotation.

**What the tests found:** all of them were measured with point-in-time stock lists, real costs and no hindsight.
- **Single-stock rules and ORB:** none beat simply holding the same stocks.
- **Fund rules:** they trade return for smaller falls, as published.

**What was missing:** a definition of what result would be good enough. Without one, research keeps going and data
keeps growing without a decision.

## 1. The money

- **Capital for automated trading:** **to confirm** (for example $10,000 to $25,000 to start).
- **Benchmark:** simply holding SPY over the same months, with dividends. A strategy that does not beat this after costs,
  or does not clearly reduce risk against it, is not worth running.

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

**The rotation measured against these:** 93% of buy-and-hold's return with 64% of its worst fall. That is close to (b)
but not inside it.

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
