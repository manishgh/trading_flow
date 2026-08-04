# Trading Desk Mirror Plan — Parallel Implementation

Status: in progress
Date: 2026-08-01

Goal: bring the web workstation to parity with the interaction patterns of the best
available trading desks — TradingView's dense configurable watchlist, Bloomberg's
keyboard-first navigation, IBKR's review-before-transmit discipline (already built) —
executed as three parallel workstreams with disjoint file ownership.

## Reference patterns adopted

| Source | Pattern |
|---|---|
| TradingView watchlist | Dense table: Last, Change, Change %, Volume, Bid/Ask columns; one-click column sort; column chooser behind a gear; compact/comfortable density; row/table view switch |
| Bloomberg Terminal | Command-first: a single palette reaches any symbol or screen; keyboard row navigation; consistent Back semantics |
| Finviz Elite | Filter bar over the grid with saved presets |
| IBKR | Order review before transmit (already implemented; not in scope here) |

Constraints carried forward: token-based styling only (no inline styles, no hardcoded
hex), `[hidden]` for visibility, 44 px targets, `aria-sort` on sortable headers,
colour never the sole signal (`.value-signed`), environment lock untouched, one-brain
rule — UI reads existing services, never re-derives engine logic.

## Workstream A — Desk grid densification (agent A)

Owns: `Pages/TradeDesk.cshtml`, `Pages/TradeDesk.cshtml.cs`,
`Services/Wishlists/WishlistDeskService.cs`, `wwwroot/js/trading-flow-desk.js`,
`wwwroot/css/desk-grid.css`, `tests/ui/specs/desk-grid.spec.js`.

- Columns at TradingView parity **where the data genuinely exists server-side**:
  Last, Change, Change %, Volume/RVOL. Investigate what quote/indicator state the
  desk services can already reach; extend the desk row record only with real data.
  No fabricated or placeholder columns.
- Column chooser (gear) with `localStorage` persistence; hidden columns removed from
  the DOM, not width-zeroed.
- Density toggle: compact (28–32 px rows) / comfortable, persisted.
- Keyboard row navigation: ArrowUp/ArrowDown move an active-row highlight, Enter
  inspects, aligned with the existing `B`/`S` shortcuts; ignored while typing.

## Workstream B — Accessibility and ease-of-use polish (agent B)

Owns: `Pages/Orders.cshtml(.cs)`, `Pages/RunningTrades.cshtml(.cs)`,
`Pages/News.cshtml(.cs)`, `wwwroot/js/orders.js`, `wwwroot/js/running-trades.js`,
`wwwroot/css/polish.css`, `tests/ui/specs/polish.spec.js`.

- Sortable headers with `aria-sort` on the Orders and Positions tables, matching the
  desk pattern (link-based, URL round-trip).
- Consistent loading / empty / error / stale states across those routes; every empty
  state names the action that fills it.
- Focus management: after a filter or cancel action, focus lands somewhere announced,
  not lost to `<body>`.
- News page: keyboard-operable filter chips, result-count announcement via the
  existing polite live-region pattern.

## Workstream C — Feature combinations (agent C)

Owns: `wwwroot/js/command-palette.js`, `wwwroot/js/desk-presets.js`,
`wwwroot/css/features.css`, `tests/ui/specs/features.spec.js`.
(The `<script>`/`<link>` tags are already wired in `_Layout.cshtml` — do not edit it.)

- Command palette (`Ctrl+K`): jump to any screen by name; jump to a symbol on the
  desk (reads the already-rendered symbol rows; no new API). Full keyboard operation,
  focus trapped while open, Esc closes and restores focus.
- Saved desk filter presets: capture the current desk query string under a name in
  `localStorage`; palette and a small strip on the desk offer them. Presets are
  navigation — applying one is a plain `location` change to a stored URL, never a
  mutation.

## Coordination rules

1. File ownership is exclusive; no agent edits another's files or any shared file
   (`_Layout.cshtml`, `site.css` are frozen for agents).
2. Agents do not run builds, tests, or the app — concurrent builds collide on obj/bin.
   The parent session builds, runs the suites, and fixes integration issues after all
   three complete.
3. Each agent writes Playwright specs for its own work in its own spec file.
4. Verification gate (parent): solution build 0/0, 1,051 .NET tests, full Playwright
   suite including the three new spec files, live smoke on port 53014.

## Sources

- [TradingView watchlist column/sort/customisation behaviour](https://www.tradingview.com/support/solutions/43000653369-how-are-change-and-change-calculated-in-the-watchlist/)
- [TradingView watchlist setup guidance](https://innovatehubfinance.com/tradingview-watchlist/)
- [Bloomberg Terminal keyboard and command-line navigation](https://researchguides.worldbankimflib.org/BloombergProfessional/KeyboardAndTickers)
- [Bloomberg getting-started keyboard workflow](https://guides.library.yale.edu/Bloomberg/Bloomberg_Basics)
