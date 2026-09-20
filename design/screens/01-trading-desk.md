# 01 — Trading Desk (desktop)

Prototype: `prototypes/Trading Desk.dc.html`
Replaces: `Pages/TradeDesk.cshtml` + `Pages/TradeDesk.cshtml.cs`
Design width: 1600px. Holds down to 1330px; below that the grid drops the optional
columns first, then the rail moves under the grid at 980px.

## Purpose

Monitor wishlist candidates, read the evidence behind each verdict, and move into a
reviewed order **without leaving the page**. Two layout options ship behind one control;
pick one after using both, or keep both as an operator preference.

## Page structure (top to bottom)

### 1. Header — 52px, sticky, `z-index: 20`
`display:flex; align-items:center; gap:24px; padding:0 20px;`
`border-bottom:2px solid var(--color-divider); background:var(--color-bg)`

- Brand `TRADINGFLOW` — Archivo 800, 17px, −0.01em, `white-space:nowrap`
- Nav links — 13px, `padding:6px 10px`, `--color-neutral-800`. Active item is
  `--color-accent`, 700, with `border-bottom:2px solid var(--color-accent)`.
  Order: Desk · Positions · Orders · News · Earnings · Research · Operations
- `margin-left:auto` group:
  - Environment segmented control, `.seg` / `.seg-opt`, `min-height:28px`,
    `padding:4px 11px`, 11px/700/0.04em. `PAPER` selected; `LIVE 🔒` at
    `opacity:0.45; cursor:not-allowed` with `title` = the lock reason. Bind to
    `TradingEnvironmentService.All`; render the lock from `candidate.LockReason`.
  - Clocks: two stacked pairs, `text-align:right`, label 9.5px/700/0.06em uppercase
    `--color-neutral-700`, time 13px tabular. NY from
    `OperationalStatusSnapshot.NewYorkTime`, UTC from `ObservedAtUtc`.
  - `Alerts on` — `.btn.btn-secondary`, 28px, 11.5px, `bell` icon 14px.
  - Signed-in user name, 12px, `--color-neutral-700`.

### 2. Operational strip — 7 equal cells
`display:grid; grid-template-columns:repeat(7,minmax(0,1fr));`
`background:var(--color-neutral-100); border-bottom:2px solid var(--color-divider)`
Each cell: `padding:8px 14px; border-right:1px solid var(--color-neutral-300)` (last cell
none). Micro label above a 13px value.

| Cell | Source |
| --- | --- |
| Environment | `status.Environment` + provider |
| Market session | `status.MarketSession`, `title=MarketSessionDetail` |
| Quotes · feed | `status.QuoteStatus` + `QuoteFeed`, age from `QuoteDetail` |
| Predictor | `status.ModelStatus` |
| Entry gate | `status.AdmissionStatus`; when blocked show the count in `--color-accent-700` |
| Open P/L | `Model.TotalPl`, direction rule |
| Buying power | **gap — see api-gaps.md** |

The current page collapses these into a `<details>` when all are nominal. Keep that
behaviour if you prefer; the flat strip is shown here because at seven cells it is still
scannable and the entry gate is the one an operator must not miss.

Keep the stable ids the quote stream patches: `#DeskQuoteConnection`,
`#DeskQuoteFreshness`.

### 3. Context row
`display:grid; grid-template-columns:minmax(0,1.1fr) minmax(0,1.4fr) minmax(0,2fr) minmax(0,1.2fr) auto;`
`gap:14px; align-items:end; padding:12px 20px;`
`border-bottom:1px solid var(--color-neutral-300)`

Fields use `.field > label` (12px, `--color-text` at 70%) over `.input` at
`height:32px; padding:4px 8px`.

1. **Wishlist** `<select name="id" data-auto-submit>` — `Model.Wishlists`
2. **Strategy context** `<select name="strategyId" data-auto-submit>` — `Model.Strategies`
3. **Finviz swing screener** - a query input in 11.5px monospace. Scope is fixed to
   swing; there is no strategy-horizon selector.
4. **Search** `<input type="search" name="search">`
5. **Sync screener** — `.btn.btn-secondary`, 32px, `refresh-cw` icon

Hidden inputs carry `source`, `sort`, `dir`, `env` so filtering never drops sort state.
Price / spread range filters stay behind the existing `<details class="desk-advanced">`.

### 4. Screener result band
`display:flex; align-items:center; gap:16px; padding:9px 20px;`
`background:var(--color-accent-100); border-bottom:2px solid var(--color-divider)`

- Scope label — 11px/700/0.07em uppercase, `--color-accent-800`
- Summary — 12px `--color-accent-900`: screener name, hit count, count not yet in the
  wishlist
- Candidate chips — `.tag.tag-outline` on `background:var(--color-bg)`, 11px/700
- Right: `Synced HH:MM:SS UTC` 11px, then `.btn.btn-primary` **Add N to wishlist**

Behaviour: swing wishlist membership persists. Discovery evidence carries its own
observation time and expiry; membership alone never admits a trade. Accept a full
Finviz Elite URL, a saved screener name, or a bare query string and normalise server-side.
Back it with `ScreenerVerificationService` + `WishlistUniverseResolver`; adding uses the
existing idempotent `ImportFinviz` handler from `Wishlists.cshtml`.

### 5. View controls
`display:flex; align-items:center; gap:18px; padding:9px 20px;`
`border-bottom:1px solid var(--color-neutral-300)`

- Source tabs — one bordered group, `1px solid var(--color-divider)`, each button
  `padding:5px 12px`, 12px, count at `opacity:0.65`. Active: `--color-text` fill,
  `--color-bg` text, 700. Tabs: **All · Signals · In trade · Screener · Disagree**.
  The first four map to `TradeDeskModel.Filters`; **Screener** and **Disagree** are new —
  Screener = rows in the active screener result, Disagree = agreement flag is CONFLICT.
- `N of M symbols`, 12px `--color-neutral-700`
- Right: **Layout** `.seg` (Grid + rail / Ladder + focus), **Density** `.seg`
  (Comfortable / Compact), and **Extra columns** toggle chip with a `check` icon at
  `opacity:1` when on and `0.28` when off.

Persist layout, density and column choice per operator in `localStorage`, reconciled over
the server-rendered default exactly as the current column chooser does.

### 6. Shell
`display:grid; align-items:start;`
`grid-template-columns: minmax(0,1fr) 384px` — layout A
`grid-template-columns: 212px minmax(0,1fr) 384px` — layout B
`min-height: calc(100vh - 300px)`. Main and rail scroll independently at
`max-height: calc(100vh - 300px)`; the rail is `position:sticky; top:0`.

## Layout A — Grid + rail (default)

Table: `width:100%; border-collapse:collapse`, header `position:sticky; top:0`.

- `th` — 10px/700/0.08em uppercase, `--color-neutral-700`, `padding:7px 10px`,
  `border-bottom:2px solid var(--color-divider)`, `background:var(--color-bg)`,
  `white-space:nowrap`
- `td` — `padding:8px 10px`, `border-bottom:1px solid var(--color-neutral-300)`,
  `vertical-align:middle`
- Row hover `--color-neutral-200`; selected row `--color-accent-100` +
  `box-shadow: inset 3px 0 0 var(--color-accent)`
- Compact density: `td` `padding:3px 10px; font-size:12px`, `th` `padding:5px 10px`, and
  sub-lines hidden. Implement as `table[data-density="compact"] td { … }`.

| # | Column | Width | Sort key | Content |
| --- | --- | --- | --- | --- |
| 1 | Market | 170px | `ticker` | Ticker 13.5px/700 over `DisplayName` 10.5px |
| 2 | Last | 110px, right | `price` | Mid 13.5px/700 over signed % change with glyph |
| 3 | Bid / Ask | 120px | — | `bid / ask` 12px over quoted sizes |
| 4 | Spread | 84px, right | `spread` | `N.N bps`; `--color-accent-700` when over the ceiling |
| 5 | RVOL | 70px, right | — | optional column |
| 6 | TradingFlow | ≥210px | `eligibility` | `.tag` verdict over truncated `EligibilityReason` |
| 7 | Predictor | ≥170px | — | `FinalSignal` 12px/700 over `probability · horizon` |
| 8 | Sync | 104px | — | Agreement flag: glyph + label |
| 9 | Position | 130px, right | `pl` | Signed P/L over `qty sh @ entry` |
| 10 | Latest news | ≥200px | `news` | optional column, truncated headline over time |

Columns 5 and 10 are the optional pair. `size`, `quoteage` and `setup` from the current
`TradeDeskModel.Columns` list are folded into columns 3, 2 and 6 respectively — if
operators miss them, re-add them to the chooser rather than the default grid.

Sorting is a header `<button>` (or the existing `asp-page` link) with a caret suffix:
`▴` U+25B4 ascending, `▾` U+25BE descending, nothing when unsorted. Keep `aria-sort`.

Rows are read-only. Whole-row click selects; keep the existing ↑/↓ + Enter keyboard model
and the `Inspect` affordance for assistive tech.

## Layout B — Ladder + focus

**Left ladder** (212px, `border-right:2px solid var(--color-divider)`, sticky, scrolls):
header strip 10px/700/0.07em uppercase, then one button per symbol,
`padding:7px 12px; border-bottom:1px solid var(--color-neutral-300)`. Line 1: ticker
13px/700 left, price 12.5px right. Line 2: short verdict (`ELIG`/`WATCH`/`BLOCK`)
10px/700/0.05em left, signed % right. Same selected treatment as a grid row.

**Centre focus column:**

1. **Symbol header** — `padding:16px 20px; border-bottom:2px solid var(--color-divider)`.
   Eyebrow `SELECTED MARKET` 10px/700/0.08em `--color-accent`; `h1` ticker 38px/800/−0.02em;
   `DisplayName` 13px; price 34px/800 tabular with signed % at 15px/700; then a
   right-aligned 4-cell bordered strip Bid / Ask / Spread / RVOL.
2. **Evidence pair** — `grid-template-columns:repeat(2,minmax(0,1fr))`, 1px divider
   between, 2px rule below. Left = **TradingFlow decision**, right = **Market predictor**.
   Each: 13px/800/0.06em uppercase heading with a `.tag` on the right, a 13.5px paragraph,
   then a `<dl>` at 13px/2-col.
   - TradingFlow `dl`: Setup · Detected · Severity · Authority (`Authoritative for entry`)
     from `LatestSignal.SignalType`, `DetectedAtUtc`, `Severity`.
   - Predictor `dl`: Probability · Horizon · Catalyst · Readiness from
     `MobileModelIntelligenceResponse` (`Swing.Probability`, `ResolvedHorizon` = `10b`,
     `Catalyst.Status`/`Direction`, `ReadinessStatus`).
3. **Agreement band** — `padding:12px 20px`, fill and colour from the flag table.
   Headline 15px/800/0.04em, note 12.5px.
4. **Open book** — the positions table from screen 04, scoped to all open positions.

The right rail is shared by both layouts.

## Right rail (384px)

### Selected symbol (layout A only)
`padding:14px 18px; border-bottom:2px solid var(--color-divider)`. Eyebrow + ticker
26px/800 + name on the left; price 20px tabular + signed % on the right. Then a 4-cell
bordered strip (Bid / Ask / Spread / RVOL) at `padding:5px 9px`, label 9px, value 12.5px.

Below it the evidence pair repeats as two 50/50 cells at `padding:11px 14px`
(heading 9.5px/700/0.07em, tag, 12px paragraph, 11px meta line), then the agreement band
at `padding:9px 14px`.

### Order ticket
`padding:14px 18px; border-bottom:2px solid var(--color-divider)`

- Heading `ORDER TICKET` 13px/800/0.06em with `PAPER · SERVER REVALIDATES` 10.5px on the right.
- **Side tabs** — 2-col bordered group, `padding:8px 12px`, 12.5px/700, flush left.
  BUY selected = `--color-text` fill. SELL selected = `--color-accent` fill. Both on
  `--color-bg` text. **Render SELL only where a tracked position exists**, exactly as the
  current page does, so the control cannot invite an accidental short.
- **Fields** — 2-col, `gap:10px`, all `.input` at 32px: Quantity · Order type
  (limit / market / stop limit) · Limit price · Time in force (day / gtc / ioc). On BUY
  add Stop price and Take profit. Maps 1:1 to `MobileOrderPreviewRequest`
  (`Quantity`, `LimitPrice`, `StopLossPrice`, `TakeProfitPrice`, `Horizon`,
  `AllowExtendedHoursTrading`) plus `OrderType` / `TimeInForce` / `TriggerPrice` from
  `OrderTicketModel`.
  Defaults: limit = ask on buy, bid on sell; stop = limit × 0.965; target = limit × 1.07.
  Treat these as placeholders, not policy — read the real defaults from the selected
  strategy's `ExitRules.StopAtrMultiple` and `TargetRMultiple`.
- **Size presets** — four `.btn.btn-secondary` at 26px/11px: 25% · 50% · 100% · 1R risk.
  1R sizes to the account risk budget over the stop distance.
- **Derived strip** — 3 bordered cells: Notional · Risk (currency + % of equity) ·
  R multiple.
- **Gate checklist** — bordered `<ul>`, each row `padding:6px 10px`, 12px, three columns:
  13px glyph (`✓` U+2713 / `✕` U+2715), 104px label, then the value. Pass =
  `--color-text`, fail = `--color-accent-700`.
  Six rows, in this order: **Quote age · Spread · Session · Notional · Duplicate ·
  Protection**. These mirror what the server checks; render from the preview response
  (`QuoteAgeMilliseconds`, `SpreadBps`, `Session`, `Notional`, and `Rejections`) rather
  than computing them client-side.
- **Blocked note** — when any row fails: `padding:8px 10px;`
  `border-left:3px solid var(--color-accent); background:var(--color-accent-100)`, 12px
  `--color-accent-900`, naming the failing checks.
- **Actions** — stage 1 is a single `.btn.btn-primary` 38px reading
  `Review protected buy — TICKER × QTY`. Stage 2 replaces it with
  `Confirm paper buy · $N` plus an `Edit` secondary, and shows
  `Ticket token expires in 30 s…` at 11.5px underneath.
  Stage 1 → `OnPostPreview`, stage 2 → `OnPostConfirm` with the `TicketToken`. When
  blocked, the confirm button reads `Blocked — cannot submit` and posting still fails
  closed server-side.

### News
`padding:14px 18px`. Heading + a 24px `.seg` scoping **Group** / **selected ticker**.
Each item: `padding:9px 0 9px 10px; border-top:1px solid var(--color-neutral-300);`
`border-left:3px solid <sentiment>` where sentiment > 0.25 is `--color-text`,
< −0.25 is `--color-accent`, otherwise `--color-neutral-400`.
Ticker + time 10.5px, headline 12.5px/600, `provider · sentiment ±N.NN` 11.5px.
From `Model.RelatedNews` (`MobileNewsItem`), which already de-duplicates by URL and
merges tickers.

## Toast
`position:fixed; right:20px; bottom:20px;` `background:var(--color-text);`
`color:var(--color-bg); padding:12px 16px; box-shadow:var(--shadow-lg)`.
Title 13px/800/0.05em uppercase, body 12.5px. Auto-dismiss ~5s.
Use it for: screener synced, wishlist updated, order accepted, submission refused.
Keep the existing `aria-live="polite"` region and `#SignalToast`.

## State

| State | Default | Persist |
| --- | --- | --- |
| `layout` | `rail` | localStorage |
| `density` | `comfortable` | localStorage |
| `showOptionalColumns` | false | localStorage |
| `source` tab | `all` | query string |
| `search`, `sort`, `dir`, price/spread bounds | — | query string |
| `selectedTicker` | none | query string (`ticker`) |
| `predictionMode`, `predictionHorizon` | `unified`, `auto` | query string |
| ticket side / qty / prices / stage | derived | request only, never persisted |

Live updates: quotes and signal badges patch in place from `trading-flow-stream.js`. Never
replace a focused control; never re-render the ticket while the operator is typing in it.
