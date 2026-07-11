# UI Design Optimization Plan — Mobile (MAUI) + Web (Razor) (2026-07)

**Status: PLAN ONLY — not executed.** Research + design plan for unifying and optimizing the
TradingFlow mobile and web UIs. Execute phase-by-phase with the verification protocol at the end.
Companion to the earlier mobile UX pass (P1–P5: SSE wiring, simplified forms, nav consolidation,
Running Trades) — that pass fixed *workflows*; this one fixes the *design system and code health*
underneath them.

---

## 1. Research Findings (current state)

### 1.1 Two products, visually
| | Web | Mobile |
|---|---|---|
| Brand name | "Quant Workstation" (`_Layout.cshtml`) | "TradingFlow" (`AppShell.xaml`) |
| Accent | teal `#176b87` (`site.css --accent`) | blue `#007ACC` (`Colors.xaml Primary`) |
| Gain/Loss | `--good #116d42` / `--bad #a12a2a` | hardcoded `#067647` / `#B42318` in code-behind |
| Dark mode | none (no `prefers-color-scheme`) | none (light-only) |
| Typography | Segoe UI 14px | MAUI defaults (OpenSans) |

Same app, two identities. Every semantic color exists twice with different values.

### 1.2 Web (`src/TradingFlow.Web`)
**Good foundation:** `site.css` already uses `:root` CSS variables (78 var refs), a consistent
`.page-header` pattern, semantic `PlClass()` helpers (TradeDesk), `aria-live` toast, and
`RenderSection Scripts`. Extend it — do not replace it.

**Debt:**
- **~930 lines of inline JS across 8 pages, zero shared JS files** (`wwwroot/` has only css + favicon):
  PaperJob 249, TradeDesk 177, Audit 150, Wishlists 145, OptimizationJob 142, Paper 62. Duplicated
  across pages: fetch+error handling, polling loops, **EventSource/SSE wiring (duplicated in
  Wishlists AND TradeDesk)**, money/pct/time formatters, badge/status rendering, HTML escaping.
- **4 pages carry their own `<style>` blocks** (Audit, Backtests, Paper, Wishlists) on top of
  site.css → per-page drift.
- **One breakpoint total** (`@media (max-width: 920px)`); data tables unusable on phone browsers.
- **No dark theme** despite the CSS-variable foundation making it nearly free.
- Nav: dead `Live` link (`href="#"`), no active-page state, Audit page unreachable from nav.
- Accessibility: 1 focus/overflow rule in 708 lines; tables lack `overflow-x` containers.

### 1.3 Mobile (`src/TradingFlow.Mobile`)
**Good foundation:** all 8 content pages have `RefreshView` (pull-to-refresh) + `ActivityIndicator`;
SSE client exists in `TradingFlowApiClient`; nav already consolidated to 4 tabs + More.

**Debt:**
- **UI built in code-behind**: 2,802 lines of `.xaml.cs` vs 1,437 of `.xaml`. Dynamic list items are
  constructed in C# (`new Label { TextColor = Color.FromArgb("#475467") … }`). `WishlistsPage.xaml.cs`
  = **1,092 lines** mixing view construction, timers, SSE, and state.
- **34+ hardcoded hex colors** across every page (`#067647`, `#B42318`, `#475467`, `#344054`,
  `#667085`…). It IS a coherent palette (Untitled-UI grays) — it just was never tokenized.
- `Colors.xaml` still ships **MAUI template junk**: `Magenta`, `MidnightBlue`, `Tertiary #2B0B98`,
  `Secondary #DFD8F7` — unused or conflicting with the real palette.
- `AppShell.xaml` hardcodes tab-bar colors inline instead of resources.
- **No shared controls**: every page re-implements card, status chip, metric row, section header,
  empty state.
- `AppThemeBinding` only in template defaults → dark theme impossible until colors are tokenized.

---

## 2. Design Principles (trading-app specific)

1. **One design language, two renderers.** Same brand, same palette, same semantics on web and
   mobile. A user glancing at either should read P/L, status, and freshness identically.
2. **Color is information.** Green/red are RESERVED for gain/loss. Status (running/idle/error) uses
   chips, not raw green/red text. Accent is for actions/navigation only.
3. **Numbers are the UI.** Prices/P/L use tabular numerals (`font-variant-numeric: tabular-nums` on
   web; consistent numeric formatting + right-alignment on mobile) so columns don't dance on refresh.
4. **State is always visible**: every async surface has loading / empty / error+retry states from a
   shared component, never a blank panel.
5. **Dark mode is a token swap**, not a redesign — which requires Phase U0 first.
6. **No new frameworks.** Razor stays Razor, MAUI stays MAUI, vanilla JS modules (ES modules), no
   CSS framework, no MVVM big-bang.

---

## 3. Unified Design Language (the spec)

One brand name everywhere: **TradingFlow** (web topbar keeps a "Trade Desk" page, but the brand is
TradingFlow). One accent: **`#176B87` (teal)** — it wins over `#007ACC` because the web already uses
it and it collides less with the semantic blue "info" range. (Swap acceptable if the user prefers
blue; decide once, apply everywhere.)

### 3.1 Tokens (identical values on both platforms)
| Token | Light | Dark | Use |
|---|---|---|---|
| `bg` | `#F6F8FA` | `#0F1418` | page background |
| `panel` | `#FFFFFF` | `#161D24` | cards/tables |
| `ink` | `#17202A` | `#E6EBF0` | primary text |
| `muted` | `#5F6B7A` | `#94A3B2` | secondary text |
| `line` | `#D8DEE6` | `#2A3540` | borders/dividers |
| `accent` | `#176B87` | `#4FA3BF` | actions, links, active nav |
| `gain` | `#116D42` | `#4CC38A` | positive P/L only |
| `loss` | `#A12A2A` | `#E5654F` | negative P/L only |
| `warn` | `#8A5A00` | `#D9A946` | warnings/stale data |
| `info` | `#1B4F9C` | `#6CA0E8` | neutral notices |

Spacing scale 4/8/12/16/24; radius 8 (cards) / 999 (chips); touch targets ≥ 44px (mobile);
type scale 12/13/14 (body) / 16/18 (section) / 22 (page title).

### 3.2 Where they live
- **Web**: extend `site.css` `:root` + add `@media (prefers-color-scheme: dark)` block and a
  `[data-theme]` override hook. Rename/alias `--good/--bad` → `--gain/--loss` (keep old names as
  aliases during migration).
- **Mobile**: rewrite `Resources/Styles/Colors.xaml` with the same semantic keys ×2 (Light/Dark
  values) and wire via `AppThemeBinding` in `Styles.xaml`; delete template junk colors. AppShell
  colors move to resources.

---

## 4. Phases (execute in order; each independently shippable)

### Phase U0 — Token layer (both platforms) — *prerequisite for everything*
1. Web: extend `:root`, add dark block, alias old names. No page edits yet → zero visual change in light mode.
2. Mobile: new `Colors.xaml` (semantic keys, Light+Dark), `Styles.xaml` styles point at semantic keys,
   AppShell de-hardcoded. App still renders identically in light.
3. Add shared C# helper for mobile P/L color (`PnlColors.For(decimal)`) and keep web's `PlClass()` pattern.
**Verify:** builds green; visual smoke light mode unchanged on both.

### Phase U1 — Web foundation
1. **Extract inline JS** to `wwwroot/js/` ES modules; pages keep only page-specific glue:
   - `api.js` (fetch wrapper: JSON, errors, anti-forgery header)
   - `stream.js` (EventSource with auto-reconnect + stale indicator — single SSE client for Wishlists/TradeDesk)
   - `format.js` (money, pct, signed-P/L class, relative time, HTML escape)
   - `ui.js` (status chips, toast, empty/loading/error block, table renderer helpers)
   - `poll.js` (visibility-aware polling loop: pause when tab hidden)
2. **Fold per-page `<style>` blocks** into site.css sections (or delete where duplicative).
3. Nav: remove dead `Live`, add `Audit`, add active-page underline (`ViewContext` compare), brand → TradingFlow.
4. Responsive: add 720px breakpoint; wrap all tables in `.table-scroll { overflow-x:auto }`;
   card grids collapse to single column; topbar collapses to wrap/scroll.
5. Dark mode enabled (tokens from U0) + `tabular-nums` on all numeric cells + visible `:focus-visible` styles.
**Verify:** run web, screenshot every page light+dark, wide+narrow; JS behavior identical (SSE, polling, actions).

### Phase U2 — Mobile foundation
1. **Purge hardcoded hex** from all 9 pages (`.xaml` + `.xaml.cs`) → semantic StaticResource / PnlColors.
2. **Shared controls** in `Controls/`: `CardBorder`, `StatusChip`, `MetricRow` (label+value+optional
   P/L coloring), `SectionHeader`, `EmptyStateView` (message + retry). Replace per-page copies.
3. **Slim the giant code-behind**: keep code-behind (no MVVM big-bang) but split by concern the same
   way the runners were split — e.g. `WishlistsPage.xaml.cs` (1,092) → partial files
   (`WishlistsPage.Signals.cs`, `WishlistsPage.Quotes.cs`, `WishlistsPage.Tickers.cs`) + move
   repeated view construction into the shared controls; target main file < 400 lines.
4. Dark theme flows automatically from U0 tokens; verify every page in both themes.
**Verify:** `dotnet build -f net10.0-android`; install APK; walk all tabs light+dark; pull-to-refresh
and SSE still live-update.

### Phase U3 — UX consistency pass (both)
1. **Uniform screen anatomy**: page title + health/status chip row → primary action → content cards.
   Mobile section headers standardized (16–18px, one weight); web `.page-header` used on every page.
2. **P/L semantics everywhere**: signed values, arrow or +/- prefix, `gain`/`loss` tokens only; status
   never uses raw green/red text (chips instead).
3. **Freshness indicators**: shared "last updated Xs ago / STALE" chip on every polling/SSE surface
   (web `stream.js`/`poll.js` emit it; mobile shared control).
4. Standardize empty/loading/error states via the shared components from U1/U2.
**Verify:** side-by-side screenshot review of the same data on web + mobile — identical reading.

### Phase U4 — Page-by-page application & polish (bounded, one commit per page)
Web order: TradeDesk → Wishlists → Paper/PaperJob → Backtests/Job/OptimizationJob → RunningTrades →
Audit → Warmup → Index. Mobile order: Wishlists (Desk) → RunningTrades → Paper → News → More pages.
Each page: apply anatomy/tokens/components, delete now-dead local code, screenshot before/after.

---

## 5. Verification protocol (every phase)
1. `dotnet build` web + `dotnet build -f net10.0-android` mobile — clean (warnings = errors).
2. Existing test suite stays green (`dotnet test src/TradingFlow.Tests`) — UI work must not touch
   engine/one-brain code; if a shared model changes, stop and re-plan.
3. Web: run site, browser-check each touched page in light/dark × wide/narrow; confirm SSE + polling
   + form posts still work (Wishlists quotes stream, TradeDesk desk stream, PaperJob events).
4. Mobile: deploy APK to device; walk each touched tab; verify pull-to-refresh, SSE updates, and all
   buttons (Trade/Sell/Cancel) against a running backend.
5. Commit per phase (U4: per page), push after each phase.

## 6. Non-goals (explicitly out of scope)
- No SPA/Blazor/React rewrite; no CSS framework; no component library dependency.
- No MVVM migration on mobile (partial-class + shared-controls discipline instead).
- No functional/workflow changes (those were the earlier P1–P5 pass) — this is design system,
  consistency, dark mode, responsiveness, and code health only.
- No engine/back-end changes of any kind.

## 7. Effort & risk
| Phase | Size | Risk | Value |
|---|---|---|---|
| U0 tokens | S | very low (additive) | unblocks everything |
| U1 web foundation | M | low (JS extraction is mechanical; SSE consolidation needs care) | kills ~900 lines duplication, dark mode, responsive |
| U2 mobile foundation | M–L | low-medium (many small edits; partial-split proven pattern) | kills 34+ hardcoded colors, 1,092-line page, dark mode |
| U3 consistency | S–M | low | the "one product" payoff |
| U4 per-page | M (spread) | low (bounded per page) | polish + dead-code removal |

Recommended execution order: **U0 → U1 → U2 → U3 → U4**, one commit per phase (per page in U4),
push after each.
