# Design: editable chart indicators, Desk indicator column, bid and ask buttons

Requested by the user on 22 November 2026. **Status: approved by the user ("Approve, build it") and built on 22 November 2026; see section 5 for differences.**
Branch `ui/redesign-v2`.

## 1. Why the EMA 20 does not appear (reproduced on the preview)

On the daily chart the server asks for far more history than an EMA 20 needs: 270 completed days before it shows a value
(`ChartIndicatorMath`), and the calculation must start at least 365 trading days before its 30-day block
(`ChartBarProjector.IndicatorSeed`). FPS and CRCL are too young, so the row says "Unavailable" and no line is drawn. MU, DASH
and ASTS show only a part ("Partial history"). The reason stands only in the closed settings panel.

**Change:** an average shows as soon as the effect of its starting value is below 1 %:
`period + min(250, 2.3 × (period + 1))` completed bars (never more than today, so EMA 200 keeps its 250 extra days), and the start is taken that far back (plus the same small reserve
as today) instead of always 365 days. The fixed 30-day blocks stay, so a value does not change when more history loads.

| Indicator | Bars needed now | Proposed |
|---|---|---|
| EMA 9 | 259 | 32 |
| EMA 20 / 21 | 270 | 69 / 72 |
| EMA 50 | 300 | 168 |
| EMA 200 | 450 | 450 (unchanged) |
| RSI 14 / ATR 14 | 140 / 264 | 80 / 80 (first value + 4.6 × period) |
| ADX 14 | 264 | 158 (smoothed twice) |

When an indicator still has too little history, the chart says so under the chart, for example "EMA 200: needs 450 days, FPS has
110". Strategy calculations use their own engine and do not change.

## 2. Indicators on the chart: at most 5, all editable

- At most **5 indicators** in total (today: 4 on the price plus 2 panes plus unlimited readouts). At most 2 of the 5 may
  have their own pane, so the candles keep their height.
- Each row in "Studies & settings" has a type, a **period** and an **interval**, editable in place, and a remove button.
- Periods: SMA and EMA 2–250 (and 200 as now), RSI, ATR and ADX 2–50, Bollinger 5–100 (two standard deviations stay).
  MACD stays 12/26/9. VWAP, OBV and relative volume have no period.
- Intervals: Chart, 5m, 15m, 1h, Day. An interval shorter than the candles is not offered. 5m, 15m and 1h values come from
  the minute bars the chart already loads; no extra provider request.
- Saved selections (`ema20`, `sma50:1d`) keep working; a saved list with more than 5 keeps its first 5.
- The pills under the chart show **the indicators you selected** with their values instead of the fixed SMA 20/50/200.
  Support, ATR and range pills stay.
- Desk start selection: EMA 21, SMA 50, SMA 200 (all day).

Files: `ChartIndicatorMath.cs`, `ChartBarProjector.cs`, `ChartIndicatorProfile.cs`, `ChartStudyProjection.cs`,
`ChartSeriesService.cs`, `stock-chart-indicators.js`, `stock-chart.js`, `stock-chart-transport.js`, `desk-chart-panel.js`,
`_StockChart.cshtml`, `Desk.cshtml`, styles.

## 3. Desk table

Columns: **Stock · Price now · Indicators (day) · Strategy (only when one is selected) · RVOL · ATR · Trade**

- **Price now:** bid/ask midpoint as today, with ▲/▼ and the percent since the previous close in one cell. The separate
  "Prev close" and "Today" columns go; the previous close stays in the tooltip.
- **Indicators (day):** three small chips, EMA 21, SMA 50, SMA 200, from completed daily bars. Each shows ▲ when the price
  is above it and ▼ when below, with the value and distance in the tooltip; "—" when the value does not exist. The values are already
  calculated by the server (`IndicatorSnapshot`); no extra data request.
- **Trade:** `Sell <bid>` and `Buy <ask>`, updated by the same live quotes as the row. Without a current quote the price is "—"
  and the button explains why when pressed.
- A button **opens the existing order ticket** for that stock and side. It never sends an order. All existing checks apply
  unchanged (trading paused, stock group, holdings, stale account, quote age). The same buttons in the chart header stay.
- Pressing a button does not change the sort or scroll position. Keyboard and screen-reader labels: "Buy MU at ask 610.76".

Width: at 1440 px the table grows from about 330 to about 500 px; the chart and the news column give up the space. Below 1440 px the
chips wrap under the price.

Files: `Desk.cshtml`, `watch-desk.js`, `desk-market-view.js`, `watch-desk.css` or `workspaces.css`, and the Desk row contract only if
EMA 21 is missing from it.

## 4. Verification

- Engine tests: bars needed per period; same value after reload, paging and a corrected bar; young stocks; period and interval bounds;
  more than 5 refused; old saved selections.
- Page tests and browser checks at 1280, 1440 and 1920 px, dark and light: edit EMA 20 to 21 and back, change interval, remove,
  reload; FPS, CRCL and MU; buttons with fresh quote, stale quote, paused trading, held and not held.
- Two independent reviewers; preview (10089) first, operator site after your look.
- Not included: last-trade price, orders without the ticket, changes to any strategy.

## 5. What was built differently, and why

- **Bid and ask on the buttons, price with up/down in the row:** the user's later message. "Price" is the bid/ask midpoint;
  the last trade price is not read. Quotes older than two minutes stay visible in the stale colour instead of "—", because the
  market is closed most of the day; the tooltip names their age. Without any quote the button shows "—" and explains why.
- **Day indicators on intraday candles:** before, they never appeared for downloaded history, because its receipt time is the
  download time. A value with any downloaded input is now drawn at its scheduled completion and marked as reconstructed. Values
  whose inputs all have recorded receipts still wait for the last receipt.
- **Young stocks** start at their first daily candle, but only when a completed read found no earlier candle; a failed or
  unfinished read shows nothing, because its values would move when the older candles arrive. The full run-in still applies.
- **Run-in limits:** at most 250 extra candles and 470 in total. EMA 200 (about 8 % of the starting average left at its first
  value) is unchanged; EMA 250 keeps about 17 %. ADX periods end at 40 for the same reason. MACD and OBV are unchanged.
- **Row EMA 21** comes from the engine's daily snapshot. It shows "—" below 50 daily candles and says in the tooltip when the
  history is still short. The chart needs 72 candles, so a stock with 50–71 candles has a row value but no chart line.
- **1h on intraday charts** has about 15 sessions of minutes: EMA 21 fits, EMA 50 and SMA 200 do not and say so.
- **Switching the candle interval** keeps the chosen list; an entry that equals another one on the new interval is only hidden.
- The chart column no longer covers the pills under it; a column that needs more height grows downwards.
