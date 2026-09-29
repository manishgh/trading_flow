# Desk Universe, Screeners, Portfolio And Preparation

Status: web screen 1 implemented 2026-09-29; orders screen pending
Applies to: `src/TradingFlow.Web` desk (`/TradeDesk`)

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

This checkpoint builds the main screen on the web. The orders screen is the next one.

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
confirmed against a live Finviz screener URL with that exact `s=` value and label:

- Swing patterns, bullish: Channel Up, Double Bottom, Multiple Bottom, Wedge Up,
  Triangle Ascending, TL Support, Head & Shoulders Inverse
- Momentum: New High, Top Gainers, Unusual Volume, Most Active
- Mean reversion: Oversold
- Catalysts: Upgrades, Downgrades, Earnings Before, Earnings After, Major News,
  Recent Insider Buying
- Swing patterns, neutral: Channel, Wedge
- Swing patterns, bearish: Channel Down, Double Top, Multiple Top, Wedge Down,
  Head & Shoulders

Not included, because their codes could not be confirmed: Top Losers, New Low,
Overbought, Most Volatile, TL Resistance, Horizontal S/R, Triangle Descending and
Recent Insider Selling. Add them once confirmed.

Screens saved in the Finviz browser UI cannot be listed. They must be saved locally
by name as screener presets on the Wishlists screen. Presets appear first in the
picker under "My saved screens".

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
- **Phone.** The phone treatment is unchanged: four tabs, list, then symbol, then
  ticket. The universe switch, custom query and preparation band are desktop-only,
  like the screener bar before them. The phone shows the all-wishlists view.

Boundaries kept: the predictor stays advisory; rows never submit orders; the ticket
keeps server review before confirm; the desk only reads and queues preparation, it
never warms data itself; no broker call was made.

## Not delivered in this checkpoint

- **Orders screen** as a subset of the desk (all placed orders plus the ticket).
  This is the next checkpoint. `/Orders` exists today as a separate page.
- A universe picker on the phone.
- Change and Change % columns. The quote payload has no previous close; see the
  remarks on `WishlistDeskRow`.
- Listing Finviz browser-saved screens (no Finviz endpoint exists) and the
  unconfirmed signals listed above.

## Verification

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
