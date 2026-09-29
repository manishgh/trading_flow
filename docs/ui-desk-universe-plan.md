# Desk Universe, Screeners, Portfolio, Preparation And Orders

Status: web screen 1 (desk), screen 2 (orders) and the follow-up round implemented 2026-09-29
Applies to: `src/TradingFlow.Web` desk (`/TradeDesk`) and orders screen (`/Orders`)

## Request

The operator opens the desk and wants:

1. Every followed stock from **every wishlist**, with the option to pick one wishlist.
2. **Finviz screeners** as an alternative source: either the wishlist stocks or a
   screener's stocks. Screeners find stocks for a swing strategy.
3. Live monitoring in pre-market, regular and post-market sessions, with news and
   technical state, as today.
4. News monitored across all of those stocks.
5. For any stock held: quantity, amount invested, value and profit/loss.
6. Live buy and sell from the same screen.
7. **Preparation** as part of the main screen, not a separate destination.
8. Later: a second screen that shows all placed orders and can place more, as a
   subset of the main screen.

Screen 1 is the main desk on the web. Screen 2, the Orders screen, follows in its own section.

## Research

Interaction patterns only; no vendor visual identity is copied.

| Platform | Pattern | How the desk uses it |
|---|---|---|
| thinkorswim (MarketWatch, Stock Hacker) | Scan results render in a watchlist-like grid with the same columns, sorting and order entry; results can be saved as a watchlist; a portfolio list shows symbols with positions. | A screener is a universe that renders in the same grid as a wishlist. Hits can be added to a wishlist. Held positions join the all-wishlists view. |
| IBKR TWS Mosaic | One Monitor panel holds Portfolio, Watchlists and predefined Market Scanners; selecting a symbol updates linked windows. | Wishlists and screeners sit behind one universe switch; selecting a row updates the rail (evidence, position, ticket). |
| TradingView | Many watchlists, each with a clear purpose; any list can be opened in the screener table. | The wishlist picker offers every list plus "All wishlists", and each row names the lists that hold it. |
| Webull desktop | Watchlist, trade and positions widgets side by side; extended-hours quotes switchable. | The grid, position columns and ticket share one screen; the session is shown on the strip and the preparation band. |
| Finviz Elite | Built-in "Signal" screens (`s=` parameter) alongside the user's own filters. | The screener picker offers catalogued Finviz signals next to saved screens. |

Sources:

- [thinkorswim Stock Hacker](https://toslc.thinkorswim.com/center/howToTos/thinkManual/Scan/Stock-Hacker)
- [thinkorswim Watchlist](https://toslc.thinkorswim.com/center/howToTos/thinkManual/Left-Sidebar/Watch-Lists)
- [Schwab: how to use the thinkorswim Scan tab](https://www.schwab.com/learn/story/how-to-use-thinkorswim-scan-tab)
- [IBKR Mosaic Monitor Panel](https://www.ibkrguides.com/traderworkstation/monitor-panel.htm)
- [IBKR Mosaic Layout](https://www.ibkrguides.com/traderworkstation/mosaic-layout.htm)
- [Mastering the TradingView watchlists](https://www.tradingview.com/support/solutions/43000745825-mastering-the-tradingview-watchlists/)
- [TradingView: scan a watchlist or flagged list](https://www.tradingview.com/support/solutions/43000724549-how-to-scan-watchlist-or-flagged-list/)
- [Webull: extended-hours quotes](https://www.webull.com/help/faq/423-How-do-I-toggle-extended-hours-pre-market-and-after-hours-quotes-on-or-off)
- [Finviz screener help](https://finviz.com/help/screener)

Earlier desk research (Bloomberg command palette, IBKR review-before-transmit,
Koyfin column chooser) is recorded in `docs/ui-trading-desk-plan.md` and
`docs/ui-desk-mirror-plan.md` and still applies.

### Finviz signals

Finviz has no endpoint that lists its signals or an Elite user's saved screens. The
catalogue is in `src/TradingFlow.Finviz/FinvizSignalCatalog.cs`. It includes only codes
confirmed against a live Finviz screener page whose title carries that exact label
for that exact `s=` value. All 33 below are confirmed; the grouping is the desk's own:

- Swing patterns, bullish: Channel Up, Double Bottom, Multiple Bottom, Wedge Up,
  Triangle Ascending, TL Support, Head & Shoulders Inverse
- Momentum: New High, Top Gainers, Unusual Volume, Most Active, Most Volatile
- Momentum, bearish: Top Losers, New Low
- Mean reversion: Oversold, Overbought
- Catalysts: Upgrades, Downgrades, Earnings Before, Earnings After, Major News,
  Recent Insider Buying, Recent Insider Selling
- Swing patterns, neutral: Channel, Wedge, Horizontal S/R, TL Resistance
- Swing patterns, bearish: Channel Down, Double Top, Multiple Top, Wedge Down,
  Triangle Descending, Head & Shoulders

Screens saved in the Finviz browser UI cannot be listed: Finviz has no endpoint for
them. To make one pickable, paste its URL into the desk's custom query, run it and
use **Save as preset** (or save it on the Wishlists screen). Presets appear first in
the picker under "My saved screens".

## Design

```text
Operational strip: environment · session · quotes · predictor · entry gate · open P/L (view) · portfolio · buying power
[Wishlists | Screener]
Wishlist: All wishlists · N  |  Strategy context  |  Search  |  Manage wishlists
  (screener scope: saved screens + Finviz signal groups, custom query, hits band, add-to-wishlist)
Preparation: session · ready / queued / stale / failed / not prepared · last run · [Prepare N]
Tabs: All · Observed setups · In trade · With news · Needs prep · Disagree
+-----------------------------------------------------------+------------------------+
| Market (lists) · Last · Bid/Ask · Spread · Setup ·        | Selected symbol        |
| Predictor · Sync · Prep · Position · P/L · Inspect        | quote, lists, prep,    |
|                                                           | position, evidence,    |
|                                                           | ticket (buy / sell)    |
|                                                           | News (group / symbol)  |
+-----------------------------------------------------------+------------------------+
```

- **Universe.** `DeskUniverse` resolves the monitored symbols before any quote is
  read: every wishlist (the new default, deduplicated, each row naming its lists),
  one wishlist, or a screener's hits. Only the all-wishlists view adds held
  positions that are on no list, marked "Held position". A single wishlist and a
  screener are narrowing views the operator chose, so they do not add holdings.
- **Screener as a universe.** A saved screen, a catalogued Finviz signal
  (`signal:code`) or a custom Finviz URL or query becomes the rows. Hits get quotes,
  news, positions, ranking and the ticket like any wishlist row. The band shows how
  many hits are new to a chosen wishlist and adds them on request. It never removes
  anything.
- **Portfolio.** The strip gains an account-wide Portfolio cell: open positions,
  market value, cost and open P/L with percent. The existing Open P/L cell is now
  labelled as scoped to the view. The grid splits Position (shares at average
  entry, cost and value) from P/L (money and percent). The rail shows shares,
  average entry, invested, value and open P/L for the selected symbol.
- **Preparation.** Preparation is the warmup service's job: caching candles,
  indicators and news catalysts before a session needs them. The band shows the
  session and the preparation state of every symbol on screen, with the latest
  run. It queues the ones that are not ready (not prepared, stale over 24 hours,
  or failed) with one click, and the rail can queue a single symbol. Reads are
  limited to 2 seconds. An unreachable warmup service shows as "not reachable" and
  never blocks the desk.
- **Live streams.** The desk streams quotes and activity for the exact symbols it
  rendered (`/api/v1/desk/quotes/stream`, `/api/v1/desk/activity/stream`, symbol
  list validated and capped at 300), not by wishlist id. The wishlist streams used
  by the mobile app are unchanged and share the same stream writers.
- **Links.** Every desk link, sort header, tab and redirect goes through one
  `RouteWith` helper. None of them can drop the scope, screener, sort or filter
  the operator chose.
- **Phone.** Four tabs, list, then symbol, then ticket. At the list level the phone
  also shows the universe switch, the wishlist or screener choice, strategy,
  search, the screener result and preparation, all as 44 px targets. The symbol
  and ticket levels stay on the one symbol.

Boundaries kept: the predictor stays advisory; rows never submit orders; the ticket
keeps server review before confirm; the desk only reads and queues preparation, it
never warms data itself; no broker call was made.

## Screen 2: Orders

Request: one screen that shows every placed order and can place more, as a
subset of the main screen.

### Research

| Platform | Pattern | How the Orders screen uses it |
|---|---|---|
| thinkorswim Monitor tab | Working, filled and cancelled orders in one place, with account balances and P/L; cancel from the order. | One journal with Working, Filled, Rejected and Cancelled / Expired views; cancel on the row; the desk's account cells above it. |
| TradingView account manager | Orders and Positions pages beside the order ticket; active orders can be edited or cancelled. | The journal and the reviewed ticket share the screen; Replace opens the ticket prefilled from the order. |
| IBKR Mosaic | Order entry and order management in one layout, linked to the selected contract. | Selecting a symbol in the journal opens the rail on it: quote, position, recent orders and ticket. |

Sources:

- [thinkorswim Monitor](https://toslc.thinkorswim.com/center/howToTos/thinkManual/Monitor)
- [thinkorswim Today's Trade Activity](https://toslc.thinkorswim.com/center/howToTos/thinkManual/Monitor/Activity-and-Positions/Today-s-Trade-Activity)
- [Schwab: how to use the thinkorswim Monitor tab](https://www.schwab.com/learn/story/how-to-use-monitor-tab-on-thinkorswim)
- [TradingView: positions and orders](https://www.tradingview.com/support/solutions/43000763362-positions-and-orders/)
- [TradingView: how to place an order](https://www.tradingview.com/support/solutions/43000786671-how-to-place-an-order-on-tradingview/)
- [IBKR Mosaic Layout](https://www.ibkrguides.com/traderworkstation/mosaic-layout.htm)

### Design

```text
Session · Entry gate · Portfolio · Buying power          (the desk's account cells)
Working · Filled · Rejected · Cancelled / expired · Total (live order counts)
New order [symbol ▾] [Open ticket]
Tabs: All · Working · Filled · Rejected · Cancelled / Expired
+-----------------------------------------------------------+------------------------+
| Updated · Symbol · Last · Status · Requested · Filled ·   | Ticket symbol: quote,  |
| Order · Price · Strategy · Client order · Cancel/Replace  | lists, position        |
|                                                           | Orders in SYMBOL       |
|                                                           | Order ticket (review → |
|                                                           | confirm)               |
+-----------------------------------------------------------+------------------------+
```

- **Subset of the desk.** The journal takes the grid's place. The rail is the
  desk's rail reduced to what placing an order needs: quote, lists, position, the
  symbol's recent orders and the same two-stage reviewed ticket (`_DeskOrderTicket`).
  Predictor and technical evidence stay on the desk; "Evidence on the desk" links
  there, and the desk rail links back with "Orders in SYMBOL".
- **Placing more.** The New order box takes any symbol (suggesting every wishlist
  and journalled symbol). A symbol in the journal opens the ticket on it. Replace
  stays "cancel plus a fresh reviewed ticket": it now opens that ticket beside the
  journal, prefilled with the order's limit and side, instead of a separate page.
  Sell opens only where a position is held, as on the desk.
- **Same checks.** The Orders screen posts to its own preview and confirm handlers,
  which call the same `ManualOrderTicketService` the desk uses. The environment lock
  is checked in the handlers, and a locked environment renders no New order box and
  no ticket.
- **Live.** `orders.js` keeps polling the journal in place. A Last column streams
  quotes for symbols with a working order and for the ticket symbol, through the
  desk's symbol-set quote stream.
- **Shared pieces.** The ticket model takes a desk row (no ranking run is needed to
  trade). The ticket form, the opening-price plan (`DeskTicketPlanService`, below)
  and the ticket script (`desk-ticket.js`, split out of `desk-layout.js`) are shared
  by both screens. The Orders rail has its own strategy picker, defaulting to the
  desk's default strategy.
- **Phone.** The rail stacks under the journal; with a symbol open, the ticket
  comes first. New controls meet the 44 px target.

`/OrderTicket` still exists and is unchanged; nothing on the web links to it any more.

### Verification

Tier: component. Nothing was confirmed or sent to a broker.

- Web build: 0 warnings, 0 errors.
- Focused .NET tests: 199 of 199 passed (desk universe, ticket defaults, symbol
  parsing, ranking, screener, wishlist, predictor, manual order and orders tests).
- Full Playwright UI suite: 85 of 85 passed, on a fresh UI data root and again on
  the persisted one. New `orders-screen.spec.js` covers the New order box, a review
  posting back to `/Orders` with the view kept, invalid symbols, the account strip,
  the locked environment and the desk-to-orders link. The lock spec now includes
  `/Orders?env=live`, and the live-list spec checks rows added in place link into
  the ticket and carry the Last cell.
- Full .NET suite: 1,628 passed, 39 failed. The failing set is identical to the
  39 recorded under screen 1, which fail the same way on the unchanged base commit.

## Follow-up round

Request: implement the remaining items, with no fixed stop or take-profit numbers
anywhere, then merge to main once verified and tested.

### Ticket prices: nothing fixed

The ticket used to open with a fixed 3.5% stop and 7% target when no strategy was
in context, and read the strategy's ATR multiple as a percent with fixed bounds.
Both are gone. `DeskTicketPlanService` derives the opening prices with the engine's
own code, the same path paper and live take:

- **Limit:** the order being replaced, else the inside ask, else the mid, else empty.
- **Stop:** `SignalGenerator.CreateTradeSignal` on the strategy's setup timeframe,
  then `StrategyInitialStopResolver` on its execution timeframe (atr,
  vwap_minus_atr or swing_low, as the strategy says).
- **Target:** the `StrategyOrderPlanner` rule: VWAP mode targets the execution VWAP;
  otherwise entry plus the stop distance times the target R multiple. A target not
  above the entry is not offered.
- **Market state:** the live streaming processor, warmed to the paper profile's
  `IndicatorWarmupBars`. A symbol the processor does not already track is not
  queried (querying would register a pipeline that only explicit eviction removes).
- **Empty, with a reason:** without a strategy, a quote, streamed state or warm
  indicators, the field is empty and a note under it says why. The operator enters
  it, and the server's review re-checks whatever is posted.
- **1R preset:** sizes to account equity times the profile's `AccountRiskBudgetPct`
  over the stop distance; disabled when either is unknown. It used a fixed 1% of
  the current notional before.

The quantity still opens at 1 share, as before; it is a starting count, not a price.

### Change and Change %

The Last cell shows the change against the previous session close, with the close
and its date in the tooltip, and an optional sortable Chg % column. The close comes
from Alpaca's snapshot endpoint (`/v2/stocks/snapshots`, `dailyBar` and
`prevDailyBar`) in one batched request per view, cached per symbol per New York day.
The reference is the latest daily bar dated before today in New York, which is right
before the open, in the session and after hours. The desk script recomputes the
change from every live quote. A symbol Alpaca does not answer for shows a dash.

Sources: [Alpaca snapshot API guide](https://alpaca.markets/learn/snapshot-api),
[Alpaca snapshots reference](https://docs.alpaca.markets/reference/stocksnapshots-1).
The endpoint's response format could not be fetched from this environment (Alpaca's
hosts are blocked); it is taken from those pages' published examples via search, and
the parser accepts either a symbol-keyed body or a `snapshots` wrapper.

### Finviz: saved screens and a query fix

- **Save as preset** on the desk's custom query saves the screen in view by name.
- **Fix:** a saved preset, a pasted URL or an `f=` string reached Finviz as a bare
  filter list (`/export?cap_smallover,...`) without its `f=` key, so the filters
  were likely not applied. `FinvizClient` now sends a bare list as `f=`, and pasted
  URLs keep their `s=` signal as well as their filters. This also covers the
  Wishlists screen's presets and screener verification.

### Verification

Tier: component. Nothing was confirmed or sent to a broker; no live Alpaca, Finviz
or warmup-service call was possible here.

- Solution build: 0 warnings, 0 errors.
- New .NET tests: `DeskTicketPlanTests` (atr stop and R target, VWAP target on
  either side of the entry, missing indicators, resolver rejection, too few bars,
  limit selection, risk budget, no strategy / quote / state, and an untracked
  symbol never queried), `AlpacaPreviousCloseTests` (in-session, pre-open, null
  bars, wrapper shape, New York dates, row change), the snapshot request and cache
  test, the bare-filter client test and the preset normalization tests. All pass.
- Full .NET suite: 1,655 passed, 39 failed; the same 39 as the unchanged base commit.
- Full Playwright UI suite: 91 of 91 passed, on a fresh UI data root and again on the
  persisted one. New `desk-ticket-change-preset.spec.js` covers empty ticket prices
  with reasons and a disabled 1R on both screens, change text and its live
  recompute, the phone picker at list level only, the eight new signals, and saving
  a custom screen.

## Not delivered yet

- A Volume / RVOL column: indicator state is not kept for watching rows; see the
  remarks on `WishlistDeskRow`.
- Listing the screens saved in the Finviz browser UI: Finviz has no endpoint for it.

## Verification: screen 1

Tier: component (UI and read-model change, no signal, fill, cost, risk or admission
logic changed).

Environment note: this cloud session had .NET SDK 10.0.112, not the pinned 10.0.300,
so `global.json` was lowered locally for the run and not committed. Playwright used
a local, uncommitted config pointing at the pre-installed Chromium.

```bash
dotnet build src/TradingFlow.Web/TradingFlow.Web.csproj
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --filter "FullyQualifiedName~DeskUniverseTests|FullyQualifiedName~UniverseRankServiceTests|FullyQualifiedName~FinvizClientTests|FullyQualifiedName~WarmupServiceTests|FullyQualifiedName~Screener|FullyQualifiedName~Wishlist|FullyQualifiedName~MarketPredictor"
cd tests/ui && npx playwright test
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj
```

Results:

- Web build: 0 warnings, 0 errors.
- Focused .NET tests: 118 of 118 passed, including the new `DeskUniverseTests` (universe
  membership, held positions, portfolio totals, signal catalogue, preparation
  states, stream symbol parsing).
- Full Playwright UI suite: 79 of 79 passed, on a fresh UI data root and again on
  the persisted one. This includes the new `desk-universe.spec.js` and the desk
  workstation, capability, authorisation (the new desk stream routes require a
  session), responsive (320 to 1440 px, 44 px targets) and live-list specs.
  One existing Wishlists-page test ("the management table no longer renders one
  form per row") depends on the persisted UI data root. It failed after extra
  manual seeding gave the managed list more than three rows, and passed again
  from a fresh data root. The Wishlists page is not changed here. Two specs were updated on purpose: the strip now has 8 cells,
  and the first wishlist option is now "All wishlists".
- Full .NET suite (run for extra evidence): 1,620 passed, 39 failed. The 39
  failures are in `SharedNewsPublicationConsumerTests` (24),
  `EvidenceDatasetCollectionRequestTests` (8), `SharedNewsImportCommandTests` (3),
  `EvidenceCollectionRequestAdapterTests` (2), `ConfigCatalogServiceTests` (1) and
  `DurableFileJobRequestTests` (1). The same 39 fail identically on the unchanged base
  commit in this Linux container, so this change did not cause them. Their cause was
  not investigated here.
- Not run: live Finviz reads (no `FINVIZ_API_KEY` here), a running warmup service,
  live Alpaca quotes and any broker call.
