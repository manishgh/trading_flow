# 05 — Trading Desk (mobile)

Prototype: `prototypes/Trading Desk Mobile.dc.html`
Target: `src/TradingFlow.Mobile` (Android), or a responsive breakpoint of `/TradeDesk`.
Design frame: **390 × 844** logical px.

## Purpose

The desk reduced to what a phone can do responsibly: read the book, read the evidence for
one symbol, and place a reviewed order. No grid, no column chooser, no layout switch.

## Frame

`display:grid; grid-template-rows: auto auto auto minmax(0,1fr) auto; overflow:hidden`

1. Status bar — 9px 18px 5px, 12px/700 tabular, clock left, wifi + battery Lucide icons
   right. Native chrome on device; drawn here only so the frame reads correctly.
2. App header — 2px bottom rule
3. Summary strip — 3 cells
4. Scrolling content (`min-height:0`, `overflow-y:auto`, no visible scrollbar)
5. Bottom bar — tab bar, or the submit bar on the ticket

Set `box-sizing:border-box` explicitly on any control that resets with `all:unset`,
otherwise full-width rows overflow by their horizontal padding.

## Header
`padding:4px 16px 10px; border-bottom:2px solid var(--color-divider)`
Left: `TRADINGFLOW` 15px/800/−0.01em over `wishlist name · HH:MM NY` 11px
`--color-neutral-700`.
Right: `PAPER` `.tag.tag-neutral` 10px/700, then market session with a `clock` icon at
11px/700.

## Summary strip — 3 cells
`background:var(--color-neutral-100)`, 2px bottom rule, each `padding:7px 14px` with 1px
vertical rules. Micro label 9px over a 15px tabular value.
Open P/L (direction rule) · Eligible (count) · Entry gate (`N block` in
`--color-accent-700`).

## Level 1 — market list

Filter tabs: 4 equal `flex:1` buttons, `min-height:44px`, 12.5px, centred, 1px right
rules. **All · Signals · In trade · Disagree**, each with a count at `opacity:0.6`.
Active = `--color-text` fill.

Each row is a full-width `<button>`, `padding:11px 16px; min-height:64px;`
`border-bottom:1px solid var(--color-neutral-300)`. Selected/last-viewed row gets
`background:var(--color-accent-100)` + `box-shadow: inset 3px 0 0 var(--color-accent)`.
Three lines:

1. Ticker 16px/700 + name 11px truncated (left); price 15px tabular over signed % 11.5px
   (right)
2. Verdict `.tag` 9.5px/700 + agreement flag (glyph + label) 10px/700 (left);
   position P/L 11.5px tabular (right)
3. `EligibilityReason` 11px `--color-neutral-700`, truncated to one line

Footnote at the end of the list, 11px: "Rows are read-only. Tap a symbol to inspect its
evidence and open a reviewed ticket."

Tapping a row → level 2.

## Level 2 — symbol evidence

Back control: full-width button, `min-height:44px`, `chevron-left` icon, 12.5px/700
`--color-accent-700`, reading `All markets`.

**Symbol header** `padding:13px 16px; border-bottom:2px solid var(--color-divider)`:
ticker 30px/800/−0.02em over name 12px (left); price 24px/800 tabular over signed %
13px/700 (right). Then a 4-cell bordered strip Bid / Ask / Spread / RVOL, label 8.5px,
value 12px.

**Agreement band** `padding:9px 16px`, fill and colour from the flag table: glyph +
headline 11.5px/800/0.05em, then a short note at 11px.

**TradingFlow decision** `padding:12px 16px`: heading 11.5px/800/0.07em uppercase with a
`.tag`, `EligibilityReason` at 13px/1.45, then `setup · detected` at 11.5px tabular.

**Market predictor** — same shape, plus `probability · horizon · read-only evidence` on the
meta line. That last phrase stays: on a small screen the two panels look equal, and they
are not.

**Open position**, only when one exists: `qty shares @ entry` 12.5px left, P/L 15px
tabular right, `ProtectionSummary` 11.5px underneath.

**Symbol news**: up to 3 items, `padding:9px 0 9px 10px;`
`border-top:1px solid var(--color-neutral-300); border-left:3px solid <sentiment>`.
Provider + time 10px, headline 12.5px/600.

**Floating action** — `position:absolute; left:16px; right:16px; bottom:70px`,
`.btn.btn-primary`, `min-height:52px`, 15px, flush left, `--shadow-lg`.
Reads `Review protected buy · TICKER`, or `Open ticket · TICKER` when a position exists.

## Level 3 — ticket

Back control reads `TICKER evidence`.

Head: `h1` `Protected buy TICKER` / `Exit TICKER` 20px/800, then at 11.5px: "The server
revalidates quote, spread, session, account, exposure and duplicate controls before
submission."

**Side tabs** — 2 equal buttons, `min-height:48px`, `padding:12px 14px`, 13px/700/0.04em,
flush left. BUY selected = `--color-text` fill; SELL selected = `--color-accent` fill.
As on desktop, offer SELL only where a tracked position exists.

**Fields** — `padding:13px 16px; gap:11px`. All inputs `min-height:46px; padding:8px 11px;`
`font-size:16px` — 16px prevents iOS zoom-on-focus, and 46px keeps the target comfortable.
Quantity (`inputmode="numeric"`) full width; then size presets 25% · 50% · Max · 1R as
38px buttons; then Limit price (`inputmode="decimal"`) and Time in force side by side; then
Stop price and Take profit side by side on BUY only.

**Derived strip** — 3 cells: Notional · Risk · R multiple, label 9px, value 14px tabular.

**Gate checklist** — one row per check, `padding:8px 16px`, 12.5px: 13px glyph, 88px label,
then the value. Same six checks and same colours as desktop.

**Blocked note** — `padding:11px 16px; background:var(--color-accent-100)`, 12px
`--color-accent-900`.

**Submit bar** — replaces the tab bar. `padding:11px 16px 14px;`
`border-top:2px solid var(--color-divider)`. Full-width `.btn.btn-primary`,
`min-height:52px`, 15px, flush left: `Confirm paper buy · $N`, or
`Blocked — cannot submit`. Footnote at 11px, either the 30s token expiry note or
"Clear the failing checks above, or pick another symbol."

## Tab bar (levels 1 and 2)
5 equal cells, `min-height:56px`, 2px top rule. Lucide icon 19px centred over a 10px label.
Desk (`area-chart`, active `--color-accent`) · Positions (`briefcase`) · Orders
(`arrow-right`) · Earnings (`calendar`) · Alerts (`bell`), inactive
`--color-neutral-700`.

In this prototype only Desk is built. The other four are the desktop screens; build them
for phone only if the operator actually works from the phone.

## Toast
`position:absolute; left:16px; right:16px; bottom:78px`, `--color-text` fill,
`--color-bg` text, `padding:11px 14px`, title 11.5px/800/0.06em uppercase, body 12px,
`--shadow-lg`, ~4.6s.

## Rules

- Every interactive target ≥ 44px; the primary action is 52px.
- Nothing below 11px, and nothing interactive below 12px.
- Three levels, one back path. No modals except the toast.
- The gate checklist is never collapsed or summarised. If a check fails the operator sees
  which one on the same screen as the submit button.
- Live quote ticks patch text in place. Never re-render while a field has focus.
