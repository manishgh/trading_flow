# TradingFlow Production Trading-Desk UI Plan

Status: proposed
Audit date: 2026-08-01
Audited surface: local working tree (uncommitted work included), running web instance on `http://127.0.0.1:53014`

Applies to:

- `src/TradingFlow.Web` — ASP.NET Core Razor workstation and responsive web UI
- `src/TradingFlow.Mobile` — .NET MAUI Android application

## 1. Relationship To The Previous Plan

`docs/ui-design-optimization-plan.md` (UI0–UI6) is complete. It fixed reflow, landmarks, focus,
touch targets, single-scroll native screens, order review safety, and the read-only prediction
boundary. Those gains are not revisited here and must not regress.

That plan deliberately scoped out *capability*. It made the existing screens safe and accessible; it
did not make them a trading desk. This plan covers the capability gap: filtering, grouping, news
triage, order breadth, environment separation, and research/earnings depth.

## 2. Audit Method

Static review of every Razor page, MAUI page, the shared stylesheet, and the API endpoint surface,
plus live DOM measurement against the running instance with a real 50-symbol wishlist. Control
counts below are measured from served HTML, not inferred.

The running instance exited partway through the audit. It was already running at session start and
all requests issued against it were read-only `GET`s.

## 3. Measured Baseline

Per-route control inventory, measured from served markup:

| Route | Selects | Text inputs | Date inputs | Search | Sortable headers | Row checkboxes | `<style>` blocks | Inline `style=` |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `/TradeDesk` | 3 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| `/RunningTrades` | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 2 |
| `/Orders` | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| `/Earnings` | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| `/Backtests` | 5 | 7 | 0 | 0 | 0 | 0 | 1 | 25 |
| `/Wishlists` | 0 | 57 | 0 | 0 | 0 | 0 | 0 | 0 |
| `/Paper` | 4 | 7 | 0 | 0 | 0 | 0 | 1 | 17 |
| `/Warmup` | 0 | 2 | 0 | 0 | 0 | 0 | 0 | 0 |
| `PaperJob.cshtml` | — | — | — | — | — | — | 0 | 101 |

Trade Desk with a real wishlist loaded: **50 symbol rows, 5 columns** (`Market`, `Bid / Ask`,
`Eligibility`, `Position`, select), **0 sortable headers, 0 search inputs, 0 row checkboxes**.

Three observations follow directly from that table:

1. **No table anywhere in the application can be sorted.** Not the desk, not orders, not positions,
   not research snapshots, not earnings.
2. **No search input exists anywhere in the application.**
3. **No date input exists anywhere**, including on a page named *Earnings Calendar* and on the
   backtest runner, whose only time control is `LookbackDays`.

## 4. Findings Against The Stated Expectations

### 4.1 Filter information on stocks — largest gap

Trade Desk exposes exactly three controls: wishlist, strategy context, and a `source` view tab set.
The grid carries no price, change, change %, volume, RVOL, ATR, gap, VWAP distance, RSI, sector, or
market-cap column — only bid/ask and an eligibility label. With 50 rows and no sort, no search, and
no numeric filter, cross-sectional scanning is not possible. Ranking candidates is the primary job
of a desk, and the current screen cannot do it.

### 4.2 Add stocks to group — workflow missing

Group membership is only editable on `/Wishlists`, one ticker per submit. That page renders **57
text inputs and 57 buttons** — one form per row. There is no multi-select, no bulk add, no
copy/move between groups, no bulk paste, and no way to add a symbol to a group from the Desk,
Earnings, News, or a backtest result. Finviz import exists and is the only bulk path.

### 4.3 Filter news — no filters, and no web news page

`/News` does not exist on web. News appears only as a wishlist timeline and a selected-symbol block
on Trade Desk, with **zero filter controls**. Android `NewsPage` offers a ticker `Entry` and
`Refresh` and nothing else. Meanwhile the news records already carry `category`, `sentimentScore`,
`sentimentLabel`, `provider`, `source`, and `whyItMatters` — the filter dimensions exist in the data
and are simply not surfaced.

### 4.4 Place orders — buy-only, limit-only, bracket-required

`ManualOrderTicketService.Normalize` hard-rejects any side other than buy
(`src/TradingFlow.Web/Services/ManualOrderTicketService.cs:285`), and requires a stop below entry
and target above entry. The sell path is already partially plumbed — buying-power and
open-position-quantity checks branch on `side == "sell"` at lines 230–234 — but validation blocks it
before it can be reached.

Consequences: no manual exit or scale-out, no market/stop/stop-limit order, no unbracketed entry, no
TIF choice (time in force is displayed in preview but never selected), and no cancel/replace for a
working order. The order ticket is also a full page navigation away from the desk, so the operator
loses the grid to place an order.

The protected-buy default is a sound safety stance and should be kept as the default. The gap is
that it is currently the *only* option.

### 4.5 Live vs paper on different screens — not implemented

There is no environment model. `"PAPER"` is a hardcoded string literal in three places:

- `src/TradingFlow.Web/Pages/TradeDesk.cshtml.cs:52`
- `src/TradingFlow.Web/Services/ManualOrderTicketService.cs:256`
- `src/TradingFlow.Web/Services/OperationalStatusService.cs:93`

Every screen is implicitly paper, distinguished only by a small text badge. No route, layout, color
treatment, or navigation separates environments.

**Binding constraint.** `docs/operating-boundaries.md:20` states that live routing remains disabled
until a frozen strategy passes research, holdout, paper-shadow, execution-calibration, and explicit
human promotion gates. This plan therefore builds the environment separation in full — real model,
separate routes, unmistakable visual identity — and renders the live surface as an explicitly
**locked, fail-closed** screen that names the outstanding promotion gate. It does not enable live
routing. That separation is what makes a later promotion a configuration decision rather than a UI
rewrite.

### 4.6 Backtest and research audit

`/Audit/{runName}` is the strongest analytical screen in the app: it has a rejection-reason
histogram, per-gate boolean chips, and three client-side filters (decision, reason, ticker). Its
problems are containment and scale:

- It ships its own hardcoded dark theme in a 190-line inline `<style>` block with literal hex colors,
  while the rest of the application is light — the page is visually foreign to the product.
- Filtering and rendering are entirely client-side over a fully materialized table; large runs have
  no pagination, virtualization, or server-side query.
- No sort, no export, and no comparison between two runs.
- Rows are built by string-concatenated HTML in a template literal.

`/Backtests` ("Quant Workstation") navigates by `onchange="window.location=" + string concatenation`
in three separate selects, saves strategy YAML behind a bare `confirm()`, hardcodes button colors
inline (`background: #0056b3`, `background: #28a745`), and presents ~20 ungrouped numeric parameters
with no help text, units, or validation. It offers `LookbackDays` but no explicit from/to date range.

### 4.7 Earnings calendar and filters

The page has one control: `Refresh`. Zero filters, zero date inputs, zero sorts.

This is the sharpest mismatch between available data and exposed UI in the codebase.
`EarningsCalendarItemResponse` already carries release window, day group, market cap, EPS estimate /
actual / surprise %, revenue estimate / actual / surprise %, result assessment, breakout assessment,
news sentiment, latest completed bar, event return %, EMA10, EMA20, MACD histogram, and slot
relative volume. The API already accepts `from` and `to` — and the page never sends them.

### 4.8 Design-system drift

The token vocabulary is duplicated: `--bg`/`--surface-page`, `--panel`/`--surface-panel`,
`--ink`/`--text-primary`, `--muted`/`--text-secondary`, `--line`/`--border-default`,
`--good`/`--state-positive`, `--bad`/`--state-negative`, `--warn`/`--state-warning`, and
`--accent`/`--action-primary` all coexist in one 1,929-line stylesheet.

Seven ad-hoc breakpoints are in use (560, 620, 640, 760, 920, 980, 1180 px) with no scale.

There is no dark mode, which is why the Audit page invented its own.

Newer pages (Trade Desk, Orders, OrderTicket, Earnings, Wishlists) are clean. The older research and
operations pages (Backtests, Paper, PaperJob, Audit) carry the drift.

### 4.9 Android

The four-tab shell (Watch / Positions / Activity / More) is correct and should be kept.

Activity and Positions already implement the right filter pattern — explicit chip buttons
(`All`/`Signals`/`System`/`Orders`, `All`/`Wishlist`/`StockPulse`/`Manual`). That pattern exists
nowhere else in the app and should be generalized.

Watch has no filter, sort, or search — only a wishlist picker and a single add-ticker entry. Each
row is a full `Border` card, so far fewer symbols fit on screen than a scanning task needs. Earnings
and News have no filters. The order ticket is buy-only with a hardcoded `Confirm paper order` label.

## 5. Reference Patterns

Interaction patterns are adopted; no vendor visual identity is copied.

| Source | Pattern adopted |
|---|---|
| Bloomberg Terminal | Command-first navigation; keyboard reaches any symbol or screen without the mouse |
| IBKR TWS / Mosaic | Order ticket as a linked side panel beside the grid, not a page navigation; explicit preview before transmit |
| Thinkorswim | Environment identity carried by full-chrome treatment, not a small badge |
| Finviz Elite / Trade Ideas | Screener filter bar over a dense sortable grid; saved filter presets |
| TradingView | Watchlist groups with fast membership editing; symbol-scoped news timeline |
| Koyfin / Fidelity ATP | Column chooser, density toggle, per-user saved layouts |

Accessibility gates from the previous plan (WCAG 2.2 AA reflow and target size, Android 48 dp,
TalkBack, 200% font scale) remain binding on every new surface.

## 6. Checkpoints

Each checkpoint is independently reviewable, test-backed, and committed separately. Ordering is
dependency-driven: the foundation and environment model unblock every later surface.

### TD1 — Design system consolidation and dark mode

Scope:

- Collapse the nine duplicated token pairs into one semantic vocabulary; delete the aliases.
- Normalize seven breakpoints to a four-step scale (compact / phone / tablet / desktop).
- Add dark mode as the default desk theme with a light option, using `prefers-color-scheme` plus an
  explicit user override.
- Delete the `<style>` blocks and inline `style=` attributes from `Audit`, `Backtests`, `Paper`, and
  `PaperJob` (190-line block plus 143 inline attributes) and re-express them in tokens.
- Extract shared JS modules for formatting, streaming, table sort/filter, and keyboard handling.

Primary files: `wwwroot/css/site.css`, `Pages/Audit.cshtml`, `Pages/Backtests.cshtml`,
`Pages/Paper.cshtml`, `Pages/PaperJob.cshtml`, `wwwroot/js/*`, MAUI `Resources/Styles/*`.

Acceptance: zero `<style>` blocks and zero inline `style=` attributes in Razor pages; one token per
semantic role; Audit renders in the shared theme; dark and light both pass contrast checks; existing
Playwright suite still green.

### TD2 — Trading environment separation

Scope:

- Introduce a first-class `TradingEnvironment` concept and remove all three hardcoded `"PAPER"`
  literals.
- Move desk, positions, and orders under an environment route segment (`/paper/...`, `/live/...`).
- Give each environment a distinct full-chrome identity — topbar treatment and accent — never a
  badge alone.
- Render `/live/*` as a locked, fail-closed surface that names the outstanding promotion gate from
  `docs/operating-boundaries.md` and exposes no order controls.
- Require explicit confirmation to switch environment; never carry an open ticket across.
- Mirror the same split and lock in the Android shell.

Acceptance: no hardcoded environment literal remains; live routes cannot render an order control
under any state; environment is identifiable from a screenshot without reading text; switching
environments discards ticket state; boundary doc gates are quoted from a single source.

### TD3 — Desk screener: filter, sort, columns

Scope:

- Server-side `DeskQuery` model: text search, price band, change % band, RVOL minimum, volume
  minimum, market-cap band, sector, signal state, position state, catalyst presence, earnings
  proximity.
- Extend the grid to a configurable column set — last, change, change %, spread, volume, RVOL, ATR %,
  gap %, VWAP distance, RSI, signal, catalyst, earnings-in, position, unrealized P/L.
- Server-side sort on every column, exposed as accessible `th` buttons with `aria-sort`.
- Quick-filter chips (All / Signal / In position / Catalyst / Earnings today) reusing the Android
  Activity chip pattern.
- Column chooser, density toggle, and saved filter presets persisted per operator.
- Virtualize the grid body; verify against 500+ synthetic symbols.

Acceptance: 500 rows sort and filter server-side without document overflow at any breakpoint; sort
state is keyboard reachable and announced; presets survive restart; mobile web keeps single-column
rows and gains the same filter set as a disclosure panel.

### TD4 — Groups and bulk membership

Scope:

- Row multi-select on the desk with a bulk action bar: add to group, remove, tag.
- Add-to-group available from Symbol Detail, Earnings rows, News items, and screener results.
- Bulk paste (`AAPL, MSFT, NVDA`), copy/move between groups, inline group creation, multi-group
  membership.
- Replace the 57-form `/Wishlists` management table with one form plus inline edit.
- Command palette (`Ctrl+K`) for symbol and screen navigation.

Acceptance: adding 25 symbols to a group takes one submit; no page renders one form per row; every
bulk action is confirmable and reversible; palette is fully keyboard operable.

### TD5 — News hub and filters

Scope:

- New `/News` web route with filters: time window, ticker/group scope, provider, source, category,
  sentiment band, catalyst-only, keyword search.
- Scope presets: my groups, my positions, single symbol.
- Chronological and grouped-by-ticker views.
- Same filter chips on the Android `NewsPage`.
- Keep the Trade Desk timeline; make it a scoped view of the same shared component.

Acceptance: every filter dimension already present in the news record is reachable from the UI;
filters compose; empty and stale states are explicit; no duplicated news-rendering code between
Desk, News, and Earnings.

### TD6 — Earnings calendar filters and views

Scope:

- Expose the existing `from`/`to` API parameters as a date-range control with week and day presets.
- Filters: release window, day group, result assessment, breakout assessment, market-cap band, EPS
  surprise range, revenue surprise range, RVOL, has-news, sentiment band, provider, and "only my
  groups".
- Sort by scheduled time, market cap, surprise %, RVOL, and event return.
- Calendar-week grid view alongside the existing chronological list.
- Row actions: add to group, open on desk, review order.
- Android: filter chips plus a date scroller.

Acceptance: every field on `EarningsCalendarItemResponse` that is a plausible filter dimension is
filterable or sortable; date range round-trips through the URL; the page no longer ships `Refresh`
as its only control.

### TD7 — Order ticket breadth

Scope:

- Keep protected bracketed buy as the default and pre-selected path.
- Enable the sell/exit ticket, activating the existing sell branches in `ManualOrderTicketService`.
- Add order type (market, limit, stop, stop-limit) and explicit time-in-force selection, each
  gated by broker and session eligibility.
- Allow an unbracketed entry only behind an explicit, separately confirmed choice.
- Render the ticket as a right-rail panel beside the grid instead of a page navigation.
- Add cancel and replace for working orders from `/Orders`.
- Keyboard: `B` and `S` open the ticket for the selected symbol.

Acceptance: server-side review, token revalidation, and fail-closed behavior are unchanged and still
cover every new side and order type; no ticket submits without an explicit confirm; duplicate taps
cannot create duplicate intents; exits are reviewable in the same shape as entries.

### TD8 — Research and backtest audit depth

Scope:

- Server-side filter, sort, and pagination for decision records; remove full-table client rendering.
- Replace template-literal HTML row construction with a shared renderer.
- Decision funnel: candidates → evaluated → per-gate survivors → entries → exits, answering "why did
  nothing trade".
- Show matched value beside threshold for every gate, per `AGENTS.md`'s audit requirement.
- Run comparison: select two or more runs, diff metrics, overlay equity curves.
- CSV/JSON export of decision records.
- `/Backtests`: replace `window.location` string navigation with a real form; group parameters into
  labelled fieldsets with units, help, and validation; add an explicit from/to date range; replace
  the bare `confirm()` on YAML overwrite with a reviewed save.

Acceptance: a 100k-decision run filters and pages without freezing the browser; every rejection
shows matched value and threshold; two runs can be compared on one screen; no destructive YAML write
occurs without an explicit reviewed confirmation.

### TD9 — Android parity and release gates

Scope:

- Bring Watch to parity: filter chips, sort, search, compact row density, multi-select add-to-group.
- Apply the shared filter component to News and Earnings.
- Environment separation and live lock in the shell.
- Sell/exit ticket parity.
- Run the full manual gate set: physical-device TalkBack, Android Accessibility Scanner, 200% font
  scale, narrow-phone and tablet layouts.

Acceptance: the manual gates in `docs/ui-verification-matrix.md` are executed on a physical device
and recorded; no high-severity Accessibility Scanner findings; parity matrix documented per feature.

## 7. Verification

Extend the existing Playwright suite in `tests/ui` rather than replacing it. New assertions:

- every data table exposes sortable headers with `aria-sort`
- every list surface exposes its documented filter set
- filter and sort state round-trips through the URL
- no route renders an order control under a live environment
- no Razor page contains a `<style>` block or inline `style=` attribute
- the 320 / 390 / 768 / 1024 / 1440 px matrix stays free of document overflow on all new routes

Physical-device TalkBack, Accessibility Scanner, and 200% font-scale checks remain manual release
gates and are never inferred from a successful build.

## 8. Constraints Carried Forward

1. UI code manages events, validation display, partial updates, and API calls only. Filtering,
   ranking, and validation belong in services and the engine.
2. Backtest, paper, and live continue to share one evaluation and execution decision path.
3. Live routing stays disabled. TD2 builds the separation; it does not open the route.
4. No React, Blazor, SPA, or CSS-framework rewrite.
5. No compatibility layer for superseded UI structures; the application is not in production.
6. Accessibility gates achieved in UI0–UI6 must not regress.
7. Preserve `tradingflow.db`, paper state, local secrets, and cached candle data.
