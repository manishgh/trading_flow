# 04 — Positions, Orders, Wishlists, Operations

Prototype: `prototypes/Desk Operations.dc.html` — one file, four screens behind the top nav.
Replaces: `Pages/RunningTrades.cshtml`, `Pages/Orders.cshtml`, `Pages/Wishlists.cshtml`,
and the health portion of `Pages/Paper.cshtml`.

In production these stay **four separate Razor pages** — they are grouped in one prototype
only so the shared chrome is written once. Each keeps its own route, query-string state and
JS module.

Shared chrome: the 52px sticky header from screen 01, with Positions / Orders / Wishlists /
Operations as nav items, the PAPER/LIVE segmented control, a UTC clock, and a `Refresh`
button with a `refresh-cw` icon.

Shared table style: identical to the desk grid — `th` 10px/700/0.08em uppercase sticky,
`td` `padding:8px 10px` with 1px `--color-neutral-300` rules, hover
`--color-neutral-200`. Any table whose minimum content width can exceed its column
**must** be wrapped in an `overflow:auto` container.

---

# 4a. Positions — `/RunningTrades`

## Page head
`padding:15px 20px 12px`. Eyebrow `TRADE BOOK`, `h1` **Positions** 27px/800/−0.02em,
standfirst 13px:

> One live book for wishlist, Stock Pulse and manual paper positions. Values refresh every
> 10 seconds without replacing a focused control.

Right: `Open desk` secondary button.

## Summary band — 5 cells
`background:var(--color-neutral-100)`, 2px rules above and below, each
`padding:10px 18px; border-right:1px solid var(--color-neutral-300)`.
Micro label over a 22px/800 tabular value.

| Cell | Source |
| --- | --- |
| Total open P/L | `TotalUnrealizedPl`, direction rule |
| Open trades | `Trades.Count` |
| Gross exposure | Σ `CurrentPrice × Quantity` |
| Unprotected | count where no broker stop is on file — `--color-accent-700` when > 0 |
| Slots used | `N of MaxConcurrentPositions` |

**Unprotected** and **Gross exposure** are additions to the current page. Unprotected is
the one an operator must see first: a position whose entry bracket never landed is an
uncontrolled risk, and it is currently invisible.
Slots used must read against the real `MaxConcurrentPositions` — if the count exceeds the
cap the page is asserting a broken risk control, so surface it as a block rather than a
plain figure.

## Source filter
Chip strip (All · Wishlist · Stock Pulse · Manual) with counts, from
`RunningTradesModel.Sources`. Sort state travels with the chip so changing source never
discards the chosen column.

## Table
Ticker (with side on a sub-line) · Source (`.tag.tag-neutral`) · Qty · Entry · Last ·
P/L · P/L % · Strategy · Protection · Run · action.

- Sortable: `symbol`, `entry`, `pl`, `plpct` (as today)
- P/L and P/L % take the direction rule; P/L is 700
- Protection renders `ProtectionSummary`; when there is no stop on file the text goes
  `--color-accent-700`
- Run shows `Reference` at 11.5px `--color-neutral-700`
- Action: `Review exit` secondary, 26px/11px

Footnote, 11.5px: "A locked environment renders no exit control at all rather than a
disabled one the client could re-enable."

Live patching by `data-trade-key` continues to come from `running-trades.js`
(`data-trade-current`, `data-trade-pl`, `data-trade-pl-pct`, `data-trade-protection`).

## Exit dialog
`.dialog-backdrop` `position:fixed; inset:0; display:grid; place-items:center;`
`background: color-mix(in srgb, var(--color-text) 45%, transparent)`.
`.dialog` `width:min(520px,100%); background:var(--color-bg);`
`border:2px solid var(--color-text); padding:18px 20px; box-shadow:var(--shadow-lg)`.

Title 17px/800. Body 13px. A `<dl>` with Environment · Action · Protection. Then the
warning at 11.5px: "An exit carries no stop or target. The server checks the exit quantity
against the open position." Actions: `Confirm paper exit` primary (flush left) and
`Keep as is` secondary. Posts to the existing `OnPostClose` with `closeKind`, `jobId`,
`sessionId`, `ticker`.

---

# 4b. Orders — `/Orders`

## Page head
Eyebrow `BROKER LIFECYCLE`, `h1` **Orders**, standfirst:

> Durable order state from intent through terminal state. Replace is cancel plus a fresh
> reviewed ticket, so an amended order is re-checked against quote, spread and session.

## Summary band — 5 cells
Working (`--color-accent`) · Filled · Rejected (`--color-accent-700`) ·
Cancelled / expired (`--color-neutral-700`) · Total today.
From `WorkingCount`, `FilledCount`, `RejectedCount`, `ClosedCount`.

## Status filter
Chip strip with counts from `OrdersModel.Filters`, carrying sort state.
Right-aligned, 11.5px: "Latest persisted state · checking every 10 s".

## Table
Updated (UTC 11.5px tabular over local time 10.5px) · Symbol (with side sub-line) ·
Status · Requested · Filled · Order (`OrderType / TimeInForce`) · Price · Strategy ·
Client order (10.5px monospace `--color-neutral-700`) · actions.

Status `.tag`: `.tag-accent` for working states (New, Partially filled, Cancel pending),
`.tag-neutral` for Filled, `.tag-outline` for Cancelled / Expired / Rejected. Use
`OrdersModel.StateLabel` so `PartiallyFilled` and `CancelPending` read as two words.

Filled quantity colour: `--color-neutral-700` at zero, `--color-accent-700` when a partial
fill, `--color-text` when complete. A partial fill is a state that needs a decision, so it
is marked.

Price uses the existing `PriceSummary` precedence: fill, then limit, then stop, else
`Market`.

Actions render **only** when `OrdersModel.CanCancel(item) && EnvironmentState.IsEnabled`:
`Cancel` (secondary with `border-color:var(--color-accent); color:var(--color-accent-700)`)
and `Replace`. Terminal rows show `Terminal` / `Terminal · no action` at 11px
`--color-neutral-500` instead of empty space.

## Cancel dialog
Same shell as the exit dialog. Body names the symbol, side and quantity, and states: "The
broker may still fill it before the cancel is acknowledged." Footnote: "Broker writes are
idempotent and audited. The order keeps its client order id through the cancel."
`Replace` does not open a dialog — it navigates to a fresh ticket, and says so.

---

# 4c. Wishlists — `/Wishlists`

## Page head
Eyebrow `UNIVERSE MANAGEMENT`, `h1` **Wishlists**, standfirst:

> Create groups, maintain ticker context, control observation, and import a Finviz universe.
> This page cannot submit an order or start a strategy.

Right: `Open desk` **primary** button — the whole point of this page is to feed the desk.

## Group tabs
A full-width strip with 2px rules above and below. One button per wishlist,
`padding:11px 18px; border-right:1px solid var(--color-neutral-300)`, left-aligned:
name 13px on line 1, `N active · observer running|paused` 11px at `opacity:0.7` on line 2.
Active = `--color-text` fill.
Right-aligned: a 200px `New wishlist name` input and an `Add new` secondary button.

This replaces the current combobox + datalist. A datalist gives no sense of how many
groups exist or which is observed; three or four tabs do.

## Body
`grid-template-columns:minmax(0,1fr) 320px`. Left column
`border-right:2px solid var(--color-divider)`; right rail sticky.

### Settings block
Heading + an `observerLabel` toggle button (`Pause observer` / `Start observer`) posting to
`OnPostSetObserved`. Then a 2-col grid: Name (`1fr`) and Description (`1.6fr`).
Then two toggle chips in one bordered strip — `Default wishlist` and
`Include extended hours` — each with a `check` icon at `opacity:1` when on and `0.28`
when off. **Square controls for independent booleans, never round radio dots.**

### Add symbols block
1. Single add — `grid-template-columns:96px minmax(0,1fr) minmax(0,1.4fr) auto`:
   Ticker · Name · Notes · `Add ticker`. → `OnPostAddTicker`
2. Bulk add — `grid-template-columns:minmax(0,1fr) auto`: a 2-row textarea and
   `Add all`. → `OnPostAddTickers`. Note at 11px: "Separate with commas, spaces or new
   lines. Adding is idempotent."
3. Finviz import, above a `border-top:1px solid var(--color-neutral-300)` —
   `grid-template-columns:minmax(0,1fr) auto`: a swing URL-name-or-query input
   in 11.5px monospace, and an
   `Import` button with a `download` icon. → `OnPostImportFinviz`.
   Note: "Adds returned symbols without removing existing entries."

Import and the desk screener use the same fixed swing scope and normalisation.
Source observations retain timestamps and expiry; wishlist membership is not admission.

### Symbols table
Bulk-action bar appears above the table only when something is selected:
`padding:5px 10px; background:var(--color-accent-100)`, `N selected` 12px
`--color-accent-900`, then `Remove selected` (accent-bordered secondary) and `Clear`.

Columns: select checkbox (34px, with a select-all in the header) · Ticker · Name (inline
`.input` 28px) · Notes (inline `.input` 28px) · Added · `Save`.

Keep the current single-form-for-the-whole-table pattern for bulk removal, with per-row
edit forms declared outside it — HTML forbids nesting, and the previous per-row-form
markup made bulk work impossible.

### Rail
`MANAGEMENT ONLY` heading, then: "This page cannot submit broker orders or start a
strategy. Open the desk to review live evidence first."
`<dl>` at 12.5px: Observer (with cadence) · Extended hours · Default · Active symbols ·
Last import.
Callout: `border-left:3px solid var(--color-accent); background:var(--color-accent-100)` —

> A current wishlist may drive today's operation but may not be projected backward.
> Backtests built on it are marked research-only.

Then `Delete wishlist`, accent-bordered secondary, opening the shared dialog. The dialog
states what is **not** deleted: open positions, orders and persisted signals.

---

# 4d. Operations

New screen. Assembles what `OperationalStatusService` already computes, plus the hosted
services, into one health view.

## Page head
Eyebrow `OPERATOR HEALTH`, `h1` **Operations**, standfirst:

> Authoritative server state. Anything unreadable is reported as unknown rather than
> guessed, and execution stays fail-closed while it is.

Right: a `.tag` summarising, e.g. `3 blocks open · 1 subsystem needs attention`.

## Summary band — 4 cells
Environment · Market session · Broker sync · Entry gate. Label 9.5px, value 17px/800,
detail 11.5px. A cell in a bad state takes `background:var(--color-accent-100)` and
`--color-accent-700` for the value — the entry gate is the one that changes colour most
often and matters most.

Sources: `EnvironmentState.Label`, `MarketSession`/`MarketSessionDetail`,
`BrokerStatus`/`BrokerDetail`, `AdmissionStatus`/`AdmissionDetail`.

## Body
`grid-template-columns:minmax(0,1.35fr) minmax(0,1fr)`. **Both children need
`min-width:0`**, and the subsystems table must sit inside an `overflow:auto` wrapper —
without it the table's min-content width overflows into the right column at anything under
about 1330px.

### Subsystems table
Columns: glyph (26px) · Subsystem (160px) · State (114px) · Evidence (≥220px) ·
Checked (96px).

Glyph: `✓` `--color-text` healthy · `✕` `--color-accent-700` failing · `–`
`--color-neutral-700` not applicable.
State `.tag`: `.tag-neutral` healthy, `.tag-accent` failing, `.tag-outline` n/a.
Subsystem cell shows the display name over the **owning type** at 10.5px — an operator
filing a bug should not have to guess which service produced the line.

| Subsystem | Owner | Evidence to show |
| --- | --- | --- |
| Alpaca credentials | `AlpacaCredentialProvider` | `IsConfigured`; result of the read-only account check |
| Market calendar | `AlpacaBrokerClient.GetSessionAsync` | trade date and session; "calendar unavailable" fails closed |
| Quote stream | `OperationalStatusService` | newest quote age; fresh ≤ 20s, delayed ≤ 2m, then stale |
| Order stream | `IOrderSynchronizationCoordinator` | `StreamConnected`; last event time and client order id |
| Account reconciliation | `IAccountReconciliationService` | `InitialReconciliationCompleted`, `Status` |
| Entry gate | `IEntryAdmissionControl` | `EntriesAllowed` and the block count |
| Market predictor | `MarketPredictorHttpClient` | `GetHealthAsync().Status`, contract version, modes answering |
| FinBERT sentiment | sidecar / `FINBERT_SENTIMENT_URL` | unset ⇒ VADER fallback (n/a, not a failure) |
| Warm-up service | `WarmupServiceClient` | manifest timestamp, window |
| Raw archive | `IRawArchiveWriter` | archived-before-parse confirmation, document count |
| Database backup | `DatabaseBackupHostedService` | last successful backup; a missed schedule is attention |

The quote thresholds (20s / 2m) are real constants in `OperationalStatusService` —
`FreshQuoteAge` and `StaleQuoteAge`. State them in the evidence text so the operator knows
what "delayed" means.

### Entry gate panel
Standfirst: "Blocks are stated with the server code so the reason is auditable, not
paraphrased." Then a bordered `<ul>`, one item per block from
`admissionSnapshot.Blocks`: `Code` in 11.5px monospace `--color-accent-800` on the left,
a severity `.tag` on the right, and `Detail` at 12px underneath.

Never rewrite `Detail` in the UI. It is the audit trail.

### Warm-up cache
`<dl>` at 12.5px: Window (and cache path) · Manifest written · Coverage (`N of M`
tickers) · Oldest observation. From `_warmup_manifest.json` via `WarmupServiceClient`.
Oldest observation should never predate the 2016 research boundary.

### Recent events
`grid-template-columns:74px 1fr`, time 11.5px tabular `--color-neutral-700`, text 11.5px
taking `--color-accent-700` for failures and `--color-text` otherwise. Rows come from
order lifecycle events, admission re-evaluations, screener syncs and warm-up writes.

## Shared toast
Same as screen 01: fixed bottom-right, `--color-text` fill, `--color-bg` text, title
13px/800/0.05em uppercase, body 12.5px, `--shadow-lg`, ~5s.
