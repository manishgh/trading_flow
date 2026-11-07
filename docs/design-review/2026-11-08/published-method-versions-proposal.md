# Published-method versions of the three kept strategies: proposal

Status: **proposed, awaiting the user's approval. Nothing here is implemented.**
Date: 8 November 2026. Research only: backtests, no paper or live orders.

On 6 November the user narrowed research to three ideas and asked that each be implemented
"as used in public space and academia". The current catalog versions differ from their
sources. This proposal names the source of each, lists every remaining difference, and gives
the smallest reusable change. Each new version is a new immutable artifact; the current
versions stay unchanged as the comparison controls.

## 1. Intraday opening-range breakout: continue the approved 2 November design

Source: Zarattini, Barbon and Aziz, *A Profitable Day Trading Strategy for the U.S. Equity
Market* (SSRN 4729284). The rules, the decisions where the paper is silent, costs, pass/fail
rules and the work order are already written and approved in direction in
`docs/design-review/2026-11-02/orb-stocks-in-play-design.md`. No new design is proposed.

How `technical.opening-range-breakout@1.2.0` differs from the paper (it stays as the control):

| Rule | Paper | 1.2.0 |
|---|---|---|
| Stock selection | each day, top 20 by first-5-minute relative volume among stocks > $5, 14-day average volume ≥ 1M shares, ATR > $0.50 | a fixed saved list |
| Opening range | first 5 minutes | first 15 minutes |
| Entry | stop order at the range high/low | next-bar market order after a completed close beyond the range |
| Stop | 10% of 14-day ATR from the fill | the breakout candle's low/high |
| Exit | the close; no target | 3R target, continuation protection, deadline 5 minutes before close |
| Entries per stock per day | one | up to two |

Next step on approval: resume that work order at step 2 (screener extension), as
`technical.opening-range-breakout@2.0.0`.

## 2. Swing mean reversion: Connors RSI-2, published rules

Source: Connors and Alvarez, *Short Term Trading Strategies That Work* (2008), the RSI(2)
strategy: stock above its 200-day simple moving average; 2-period RSI closes below 5; buy;
sell when the close is above the 5-day simple moving average. The book uses no stop and no
profit target, and argues that stops lowered results on this rule. (The book's stock-selection
filters, minimum price and volume, will be checked against the text and frozen before any run.)

How `swing.connors-rsi2@7.0.0` differs (it stays as the control):

| Rule | Book | 7.0.0 |
|---|---|---|
| Entry rule | close > SMA200, RSI(2) < 5 | same |
| Market filter | none | SPY above its 200-day average |
| Stop | none | 2 × ATR |
| Target | none | 2.5R |
| Time limit | none | 5 bars |
| Exit | close > SMA5 | same |
| Fill | at the signal day's close | next open (kept: we never act on a price not yet known) |

Proposed `swing.connors-rsi2@8.0.0`: the 7.0.0 file with the market filter, ATR stop, R
target and time limit removed. The only difference from the book is the next-open fill,
recorded as a named difference.

**Required component (the only code change):** today every order is sized from its stop
distance (`account-risk-budget-v1`), so a strategy without a stop cannot be sized. Add one
generic sizing option, `position_size_pct_of_equity` (for example 10% of equity per position,
at most 10 positions), selected by a new admission profile, and a stop mode `none`. Existing
strategies keep stop-based sizing and their identity hashes. A wide emergency stop is
deliberately not proposed, because it would be a difference from the book.

## 3. Swing snapback: short-term reversal after a news-free drop

Sources: Jegadeesh (1990, *Journal of Finance*) and Lehmann (1990, *Quarterly Journal of
Economics*): stocks with the largest recent losses tend to rebound over the following weeks.
Chan (2003, *Journal of Financial Economics*): the rebound occurs mainly when the drop came
without news; drops on company news tend to continue.

These papers rank whole markets. The tradable rule below uses the user's saved Finviz screen
"Mean revision snapback" as the stretch definition, so it is an operational version of the
published effect, not a replication, and is reported as such.

Proposed `swing.snapback@1.0.0` (new strategy ID; the interim
`technical.recovery-sma5-news-avoidance@1.0.0` stays as the comparison):

- Universe: price above $20, average volume above 2M shares, mid-cap and larger (the screen's
  liquidity terms, applied point in time in backtests).
- Entry gate: RSI(14) below 30 **and** close at least 10% below its 20-day simple moving
  average. No 200-day trend filter (this is what separates snapback from Connors).
- News veto: no company article in the 120 hours before the decision (existing
  `avoid_recent_company_news` treatment, with its stated research limitations).
- Entry: next open. Exit: close above the 5-day average, or after 5 trading days (the weekly
  horizon of Lehmann 1990), whichever comes first. No stop or target, sized like 8.0.0 above.

**Required component:** one generic entry gate, "close at least X% below its N-day simple
moving average" (`min_close_below_sma_pct` with `close_below_sma_period`), with unit tests.
Everything else reuses existing rules.

## Verification after implementation

- Focused tests for each new component; existing identity hashes unchanged.
- Each new version runs through the normal saved-plan workflow on the same frozen window and
  list as its control, reported monthly and separately per strategy.
- Pass/fail rules frozen before reading results; six months of data can at most give
  RETAIN_RESEARCH.
