# Opening-range breakout on stocks in play: design

Status: direction approved by the user on 2 November 2026; revised after an independent code review
and an independent quant review the same day. Research only: backtests, no paper or live orders.

## Published rules, checked against the paper

Zarattini, Barbon and Aziz, "A Profitable Day Trading Strategy for the U.S. Equity Market"
(SSRN 4729284), sections 2.1 and 4:

- Data: 2016 to 2023, US stocks on NYSE and Nasdaq including delisted companies (CRSP); intraday
  data from IQFeed, not adjusted for splits or dividends. The simulation bar size is not stated.
- Daily filters: opening price above $5; average volume over the previous 14 days of at least
  1,000,000 shares; 14-day ATR (Wilder's average true range) above $0.50.
- Relative volume: volume of the first five minutes (09:30-09:35 New York) divided by the average
  first-five-minute volume of the previous 14 sessions. At least 100%; trade the 20 highest.
- Direction from the first five-minute candle; no order on a doji (open equals close).
- Entry: stop order at the opening-range high (long) or low (short).
- Stop: 10% of the ATR from the executed entry price. No profit target. Close at 16:00.
- Size: lose 1% of equity if the stop is hit, with a 4x gross leverage limit. The paper does not
  say how the limit is shared between positions; its footnote says some trades therefore risk
  less than 1%.
- Costs: $0.0035 per share.

## Decisions where the paper is silent (frozen, each listed as a difference in results)

- Leverage sharing: positions are admitted in trigger order and each is capped so total gross
  exposure stays at or below 4x equity. Alternative reported: equal cap of 4x/20 per position.
- Bars: one-minute bars for selected stocks on their selected day, so entry and stop order inside
  a five-minute bar is known. If entry and stop fall in the same minute, the trade counts as a loss.
- Exit price: the official closing auction price (daily close).
- Order stays live until the close; no re-entry after a stop; ties ranked by issuer identifier;
  fewer than 20 qualifiers means fewer trades.
- Opening auction trade is included in the first five-minute volume on every day alike.
- Stocks with fewer than 14 prior sessions are not eligible; no first-bar trade means zero volume.

## Periods

User decision, 2 November 2026: day-trading research uses six months of five-minute bars, and
swing research one year of daily setup bars with one-hour entry bars.

- Study window: 1 May 2026 to 30 October 2026 (about 125 sessions), plus 14 earlier sessions
  for the relative-volume and ATR baselines. Daily bars are collected from 1 September 2024 so
  daily indicators are warmed before any decision.
- This window is after the paper and never seen by its authors, so it is a genuine test period.
  There is no 2016-2023 replication, so an implementation defect cannot be told apart from decay
  of the edge by comparing with the paper; the paper's own published figures remain the only
  reference.
- Six months is one market period. A pass here can retain the strategy for further research or
  extend the window; it cannot advance it to paper trading.

## Stock selection

- Paper and live (later, separate approval): Finviz Elite
  `ind_stocksonly,sh_price_o5,sh_avgvol_o1000,ta_averagetruerange_o0.5`, verified on 2 November
  2026 at 1,262 stocks. Finviz relative volume is not used; it covers the whole day.
- Backtests: the same rules recomputed for each date from data known before 09:35, through the
  existing `HistoricalScreenerUniverseProvider` and `UniverseMembershipDecision`, extended with:
  share volume, ATR and minimum-history rules; issuer identity instead of symbol text; processing
  in date chunks; ranking by a named measure as a parameter.
- Fund exclusion: equities only, using the Alpaca asset class and the issuer records; funds and
  leveraged products excluded by a frozen list of identifiers recorded with the run.
- No adjusted prices; splits are applied inside each look-back window only when the corporate
  action was known by that date. No current float, market cap, shortable flag or same-day value.
- Survivorship: Alpaca's inactive list missed 39 of 60 known delisted companies on 31 October
  2026. Each result reports the share of top-20 slots held by delisted names, compares their
  trades with survivors, and repeats with every estimated missing slot counted as -1R minus costs.

## Components (reuse first)

1. Screener extension as above, publishing a ranked list per session with value and rank.
2. A shared pure function for the first-bar relative volume, extracted from `IndicatorEngine`,
   with the averaging method as a real parameter (the paper uses the mean, production the median)
   and a new profile version so existing fingerprints stay unchanged.
3. Opening-range rules under a new policy `opening-range-breakout-recipe-v1`: range minutes,
   relative-volume minimum and look-back, entry order, stop as ATR fraction from the fill or a
   structural level, target as R multiple or none, exit at the close. No previous-session
   requirement. Coverage checked only on the days a stock is selected.
4. Execution: target optional instead of fixed 3R; stop from the actual fill; daily ATR supplied
   by the screener; exit at the close; raw-price timing policy; gross exposure cap above 100%;
   per-share commission.
5. Data through the existing evidence collection path: asset list with inactive equities, daily
   bars, corporate actions, first five-minute bars for daily-eligible stocks (about 30,000
   multi-symbol requests), one-minute bars for selected stocks only.

## Costs

Commission $0.0035 per share plus SEC and FINRA sell fees. Entry and stop exits pay half the
measured spread plus stop slippage starting at one spread, from Alpaca historical quotes at the
trigger time; gaps through a stop fill at the next price. Stress: measured, 2x, and 3x plus 20
basis points; break-even cost per trade reported. Trading halts are kept, not skipped.

Shorts: no short after a 10% fall that triggers Rule 201 unless the price allows it; Alpaca's
easy-to-borrow flag is current only, so the long-only result is reported as the executable one.

## Pass and fail rules (frozen before results are read)

- Study window: at least 1,500 trades and 120 sessions. The earlier 250-session and two-year
  rules cannot be met by six months; until the window is extended, the best possible decision is
  RETAIN_RESEARCH.
- t statistic of the daily summed-R series (equivalent to clustering by date): at least 3.0.
- Net expectancy at measured cost at least +0.10R per trade, and above zero at 2x cost.
- After the leverage cap: net Sharpe at least 1.0 and positive money return.
- Positive after removing the best 1% of trades; no month above 40% of profit.
- Top-20 selection beats no selection by a statistically significant margin.
- Stop the strategy if test-period net expectancy is zero or below, if the 2x cost case is
  negative, or if break-even cost is below the measured median spread plus slippage.

## Order of work

1. Collect the asset list (active and inactive) and daily raw bars plus corporate actions (done for the list; daily bars from 1 September 2024).
2. Extend the screener; publish daily eligible counts. Sanity check: 31 October 2026 near 1,262.
3. Collect first five-minute bars; publish the ranked daily list.
4. Collect one-minute bars for selected stocks; run the recipe evaluator.
5. Signal study of gross and net R per trade per year before portfolio changes.
6. Portfolio changes: leverage cap, per-share commission, then the full replication and test.
