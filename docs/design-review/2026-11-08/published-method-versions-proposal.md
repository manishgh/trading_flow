# Published-method versions of the three kept strategies: proposal

Status: approved by the user on 8 November 2026. All three sections are implemented as research artifacts (see "Implemented" at the end). For section 1 the user chose a rules check on the existing stock list first; the daily stock selection of the 2 November work order follows later.
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

## Implemented (8 November 2026)

New artifacts: `swing.connors-rsi2@8.0.0` (`swing-connors-rsi2-published.bt-v8.yaml`) and
`swing.snapback@1.0.0` (`swing-snapback.bt-v1.yaml`), each with its own admission profile whose
risk policy is `position-notional-no-stop-v1`. The catalog refuses a no-stop strategy with any
other risk policy, and refuses that policy on a strategy with a stop.

Components:

- `initial_stop_mode: none` and `profit_target_mode: none` (long, daily setup and execution bars
  only; no trailing stop, continuation protection or R-multiple target). No stop or target order
  is placed in the daily simulator, the daily execution coordinator or the daily SIP portfolio.
  A stop or target price of 0 in results means "no such order". Trades without a stop have no R.
  Broker order planning (paper/live) refuses these plans.
- `min_close_below_sma_pct` with `close_below_sma_period` (5, 10, 20, 50, 150 or 200), measured on
  the completed close; missing values fail closed.

Differences from the approved text, decided during implementation:

- **Sizing.** Instead of a new `position_size_pct_of_equity` option, no-stop positions reuse the
  existing notional sizing: each position gets equity divided by the maximum number of
  positions, capped by `max_position_notional_pct`. The run profile's portfolio settings
  therefore set the position size (for example 10 positions = 10% each).
- **Exit timing (both strategies).** The engine fills at the next open after a completed
  decision and evaluates exits from the bar after the entry bar. The earliest exit is therefore
  the open two sessions after entry; in the book it is the close of the first day after entry.
  Named difference, not changed, because it is shared engine timing for every strategy.
- **Stock selection.** Neither file filters price, volume or size; the run's stock list does.
  Connors's minimum price and volume terms are to be confirmed from the book before any run;
  snapback must use the point-in-time daily screen (price above $20, average volume above 2M
  shares, mid-cap and larger) rather than a hand-picked list, to avoid choosing survivors.
- **News veto timing.** The historical news diagnostic treats an article as known 24 hours after
  its provider update time (frozen research assumption). News published on the drop day itself
  is therefore not vetoed, although that is the news Chan (2003) separates out. Results must say
  so; fixing it needs reliable original receipt times.
- **Snapback ranking.** When more stocks qualify than there are free positions, the most
  oversold by RSI(2) is taken first; the papers rank by the size of the past loss.

Not run yet: no economic backtest of either version. Runs need the frozen window, stock list
and pass/fail rules first.

### Opening-range breakout 2.0.0 (implemented 8 November 2026)

`technical.opening-range-breakout@2.0.0` (`opening-range-breakout.bt-v4.yaml`, policy
`opening-range-breakout-recipe-v1`, admission profile `opening-range-breakout-recipe-research-v1`)
applies the paper's trade rules to the run's own stock list. The user chose this rules check
before collecting data for the paper's daily stock selection.

Rules: 5-minute opening range; trade in the direction of its body, no order on a doji; the
first 5 minutes' volume must be at least the mean of the same interval over the previous 14
sessions; a stop order at the range high (long) or low (short), placed when the range completes
and live until five minutes before the close; protective stop 10% of the 14-session daily ATR
beyond the trigger; no target; one filled entry per stock per session.

Named differences from the paper:

- **No daily top-20 stock selection.** The paper's edge is attributed to it, so this version
  tests the trade rules only; a hand-picked list also carries hindsight.
- **Stop measured from the trigger, not the fill.** A stop order fills at its trigger, so the two
  are the same unless the fill gaps past the trigger.
- **Exit five minutes before the close** instead of at the close, because five-minute candle
  replay fills at a bar's open.
- **Daily ATR from regular-session five-minute bars** of the previous 14 sessions (simple mean of
  14 true ranges, which equals Wilder's average with that much history).
- **Costs from the run profile**, not the paper's $0.0035 per share.
- An entry triggered inside a five-minute bar is recorded at that bar's end.
- **Entry and stop in the same five-minute bar.** The stop is tight (10% of ATR), so the bar that
  fills the entry often also touches the stop. The order of the two inside one bar is unknown,
  so the trade counts as a loss and its exit reason is `stop_loss_entry_bar_order_unknown`.
  Results must report how many trades this affects; one-minute bars (the 2 November design)
  would resolve most of them.
- **Sizing.** The run profile's account risk budget and position cap (equity divided by the
  maximum number of positions) apply, without the paper's 4x leverage. With stops this tight
  the position cap usually binds, so trades get roughly equal notional rather than equal risk.
- **The paper's daily filters are not applied** (opening price above $5, 14-day average volume
  of at least 1M shares, ATR above $0.50). On low-ATR stocks the stop is only cents wide and
  costs can exceed half the planned risk; the run's stock list must respect these filters.

Not run yet: no economic backtest of ORB 2.0.0.

### Swing shorts (approved and implemented 8 November 2026; versions 1.0.0, superseded by 2.0.0 below)

Two separate strategies, so the long versions keep their identities:

- `swing.connors-rsi2-short@1.0.0` (`swing-connors-rsi2-short.bt-v1.yaml`, admission profile
  `published-no-stop-kept-gaps-v1`): the short side of Connors and Alvarez (2008). Entry when the
  completed daily close is below its 200-day average and RSI(2) is above 95. Cover when a
  completed close is below its 5-day average, at the next open. No stop, target or time limit.
- `swing.snapback-short@1.0.0` (`swing-snapback-short.bt-v1.yaml`, admission profile
  `historical-news-presence-no-stop-v1`): the mirror of `swing.snapback`. Entry when RSI(14) is at
  least 70 and the completed close is at least 10% above its 20-day average, with no
  provider-linked company article in the frozen 120-hour window. Cover when a completed close is
  below its 5-day average or after 5 bars, at the next open. No stop or target.

Same-day candidates are ranked most overbought first (`portfolio_rank_mode: rsi2_descending`).
Both are sized by position notional, the same as the long no-stop versions.

Named differences and risks:

- **Fill at the next open**, planned from the signal close (as for the long versions).
- **A short with no stop has no upper limit on its loss.** The reported planned loss (position
  notional plus fees) understates that. Broker order planning (paper/live) refuses any plan
  without a stop, so these run only in research backtests.
- **Borrow cost and short availability are not modelled.** Hard-to-borrow stocks may be
  impossible to short or charge a daily fee; results overstate what a real account would earn.
- The two five-minute execution replays (`daily-rth5m-nextslice-ioc-v1` and
  `daily-rth-sip-portfolio-native-v1`) stay long-only and refuse these strategies with an error.

Not run yet: no economic backtest of either short strategy.

### First exit check on the fill day (approved and implemented 8 November 2026)

The simulator first checked exits on the close of the bar after the fill bar, so the fill day's
own close was never checked: a trade that already met its exit on that close was held one more
day. The book enters at the signal close and checks the next close; with a next-open fill the
nearest equivalent is the fill day's close.

New exit setting `check_exits_from_fill_day: true` (allowed only with `initial_stop_mode: none`):
the fill day's completed close is the first check of the strategy's own exits (SMA5 cross, bar
limit), a hit sells or covers at the next open, and the fill day counts as bar 1, so the 5-bar
limit holds a trade for at most 5 sessions. Stops and targets keep their timing, so Connors 7.0.0
and both ORB versions are unchanged.
Both five-minute execution replays (`daily-rth5m-nextslice-ioc-v1`, `daily-rth-sip-portfolio-native-v1`)
follow the same setting, so every replay mode gives the same exit session.

Known gap, not changed: paper trading (`LiveRunner.Exits.cs`) still starts daily exit checks after
the bar it maps the broker fill to, so it does not check the fill day's close. It does not affect
these strategies today, because broker order planning refuses any plan without a stop; it must be
aligned before any no-stop daily strategy could trade on paper. When no bar follows the fill bar
(end of data), the trade is reported open without checking the fill day's close.

New versions with the setting on: `swing.connors-rsi2@9.0.0` (`swing-connors-rsi2-published.bt-v9.yaml`),
`swing.snapback@2.0.0` (`swing-snapback.bt-v2.yaml`), `swing.connors-rsi2-short@2.0.0`
(`swing-connors-rsi2-short.bt-v2.yaml`) and `swing.snapback-short@2.0.0`
(`swing-snapback-short.bt-v2.yaml`). The superseded 8.0.0 and 1.0.0 versions were never run and
are recorded in `docs/research/retired-strategy-artifacts-2026-11-07.md`.

Not run yet: no economic backtest of any of the four.
