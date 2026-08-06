# 02 — Earnings

Prototype: `prototypes/Earnings.dc.html`
Replaces: `Pages/Earnings.cshtml` + `wwwroot/js/earnings.js`
Design width: 1600px (page uses the wider `earnings-shell` container). Rail drops under
the timeline at 980px.

Every field on this screen already exists on `EarningsCalendarItemResponse` /
`EarningsNewsEvidenceResponse`. **No new backend work is required** — the three alert
lanes are derived client-side from fields the API already returns plus the operator's open
positions.

## Purpose

Answer three questions without reading the whole calendar: what has already beaten and
broken out, what is about to report, and what is reporting that I already hold.

## Page structure

### 1. Header — 52px, sticky
Same as the desk, with **Earnings** active. Three clocks instead of two:
NY · AMS · UTC, from `ScheduledNewYorkText` / `OperatorTimeZone` / `ExchangeTimeZone`.
`Refresh` button with a `refresh-cw` icon, 28px.

### 2. Monitor strip — 5 equal cells
`background:var(--color-neutral-100); border-bottom:2px solid var(--color-divider)`,
each cell `padding:8px 16px; border-right:1px solid var(--color-neutral-300)`.

| Cell | Source |
| --- | --- |
| Monitor | `MonitoringActive` + `MonitoringIntervalSeconds` → `Running · every 60 s` |
| Last analysis | `LastAnalysisUtc` |
| Evidence window | `NewsWindowStartUtc` → `NewsWindowEndUtc` (`NewsWindowLabel`) |
| Next reporting day | `NextBusinessDateLabel` |
| Authority | static: `Advisory only · cannot route`, in `--color-accent-700` |

The Authority cell is not decoration. The monitor is advisory and cannot place an order;
say so on screen.

### 3. Attention band — 3 equal lanes
`display:grid; grid-template-columns:repeat(3,minmax(0,1fr));`
`border-bottom:2px solid var(--color-divider)`. Each lane is a `<button>` at
`padding:12px 16px; text-align:left; border-right:1px solid var(--color-neutral-300)`.
Active lane: `background:var(--color-accent-100)` +
`box-shadow: inset 0 -3px 0 var(--color-accent)`. Clicking toggles it as a filter.

Line 1: Lucide icon 19px + count 30px/800 tabular + label 12px/700/0.07em uppercase.
Line 2: the rule, 12px `--color-neutral-800`.
Line 3: matching tickers, 11.5px/700/0.04em, separated by ` · `, or `None`.

| Lane | Icon | Predicate | Colour when non-zero |
| --- | --- | --- | --- |
| Beat + breakout confirmed | `trending-up` | `EpsOutcome == "beat" && BreakoutAssessment == "confirmed"` | `--color-accent` |
| Reporting within 30 minutes | `clock` | `EpsActual == null && 0 < minutesUntil(ScheduledAtUtc) <= 30` | `--color-text` |
| Symbols you hold reporting | `briefcase` | ticker ∈ open positions (`MobileRunningTrade.Ticker`) | `--color-text` |

Copy for each rule, verbatim from the prototype:

1. "Result beat and a completed bar closed above the pre-release reference high on elevated slot volume."
2. "Confirmed or estimated slot inside the next half hour. No result evidence exists yet."
3. "Open positions in the trading group with a print today or on the next reporting day."

### 4. Filter bar
`display:grid; gap:9px; padding:11px 20px;`
`border-bottom:2px solid var(--color-divider)`. Four rows, each
`grid-template-columns:104px minmax(0,1fr); gap:12px; align-items:center` with a
10px/700/0.07em uppercase label on the left.

Chip groups are one bordered strip, `width:max-content`, each chip `padding:5px 12px`,
12px, count at `opacity:0.6`, active = `--color-text` fill / `--color-bg` text / 700.

| Row | Chips / controls | Source |
| --- | --- | --- |
| Dates | Today & next reporting day · Today · Next reporting day | `DateScope` |
| Session | All sessions · Pre-market · Market hours · Post-market | `Filters.Sessions` with `Count` |
| Result | All results · Beat · Miss · In line · Pending | `EpsOutcome` |
| Refine | Only symbols I hold · Has news evidence (chips with a `check` icon at `opacity:1`/`0.28`); Min \|EPS surprise\| % number input 74px; Sort `<select>` 172px | — |

Sort options: Scheduled time · Market cap · EPS surprise · Event move · Relative volume →
`ScheduledAtUtc`, `MarketCapMillions`, `EpsSurprisePercent`, `EventReturnPercent`,
`SlotRelativeVolume`.

Right-aligned `N of M events` at 12px.

The market-cap row from the current page (`Filters.MarketCaps`) is dropped from the
default bar to keep it to four rows — re-add it as a fifth row if operators use it.

### 5. Body — timeline + evidence rail
`display:grid; grid-template-columns:minmax(0,1fr) 400px; align-items:start`.
Timeline scrolls at `max-height:calc(100vh - 340px)` with
`border-right:2px solid var(--color-divider)`; the rail is sticky.

## Day group

Header: `display:flex; align-items:baseline; justify-content:space-between;`
`padding-bottom:7px; border-bottom:2px solid var(--color-text);`
`position:sticky; top:0; background:var(--color-bg)`.
Title 19px/800/−0.01em (`Today · Tue 04 Aug 2026`,
`Next reporting day · Wed 05 Aug 2026` from `DayGroup` / `NextBusinessDateLabel`).
Summary on the right, 11.5px, `white-space:nowrap`: `N of M events · K reported`.

Empty group: `padding:22px; border:1px dashed var(--color-neutral-400)`, 13px
`--color-neutral-700`.

## Event card

`border-bottom:1px solid var(--color-neutral-300);`
`border-left:4px solid <accent>; padding:13px 0 15px 14px`

Left rule from `ResultAssessment`:

| `ResultAssessment` | Rule colour |
| --- | --- |
| positive | `--color-text` |
| negative | `--color-accent` |
| mixed | `--color-neutral-500` |
| awaiting / insufficient data | `--color-neutral-300` |

### Identity row
Ticker 19px/800/−0.01em · `CompanyName` 13px `--color-neutral-800` · market cap 11px
(`$N.NB cap` / `$N.NNT cap` from `MarketCapMillions`).
Meta line, 11.5px tabular `--color-neutral-700`, 16px gaps:
`ReleaseWindowLabel` · `ScheduledNewYorkText` · `ScheduledUtcText` ·
`IsScheduleEstimate ? "Provider-estimated slot" : "Confirmed slot"`.

### Badges (right, wrapping, `justify-content:flex-end`)
`.tag`, 700, 0.04em, `white-space:nowrap`, in this order:

1. `HELD` — `.tag-accent`, only when the operator holds it
2. `IN N MIN` — `.tag-accent`, only when pending and ≤30 minutes out
3. `ReleaseWindowLabel` — `.tag-neutral`
4. `BEAT` / `MISS` / `IN LINE` / `AWAITING` — `.tag-accent` for BEAT, else `.tag-neutral`
   (`EpsOutcomeLabel`)
5. `BREAKOUT CONFIRMED` / `BREAKOUT POSSIBLE` / `BREAKOUT FAILED` / `NO BREAKOUT YET` —
   `.tag-accent` for confirmed, else `.tag-outline` (`BreakoutAssessment`)

### Result strip — 6 cells
`border:1px solid var(--color-divider); grid-template-columns:repeat(6,minmax(0,1fr))`,
each `padding:6px 10px`, label 9px/700/0.06em uppercase, value 13px tabular.

EPS est · EPS actual · Surprise · Rev est · Rev actual · Rev surprise
→ `EpsEstimate`, `EpsActual`, `EpsSurprisePercent`, `RevenueEstimateMillions`,
`RevenueActualMillions`, `RevenueSurprisePercent`.
Unreported actuals read `awaiting`, not `0` or `—`. Surprises take the direction colour.

### Reaction strip — 6 cells, `border-top:0` so it butts onto the result strip
Pre-release high · Latest close · Event move · Slot RVOL · EMA 10 / 20 · MACD hist
→ `PreReleaseReferenceHigh`, `LatestClose`, `EventReturnPercent`, `SlotRelativeVolume`,
`Ema10`/`Ema20`, `MacdHistogram`.

### Analysis
`AnalysisReason` verbatim at 13px/1.45, `text-wrap:pretty`. This is the server's own
explanation of the assessment — render it, do not paraphrase or truncate it.

### Held note
When the operator holds the symbol: `padding:7px 10px;`
`border-left:3px solid var(--color-accent); background:var(--color-accent-100)`, 12px
`--color-accent-900`, naming the ticker and confirming protection predates the print.

### Provenance disclosure
`<details>` with a `chevron-right` icon, summary 12px/700 `--color-accent-700`,
"Provenance and previous quarter". Body is two `<dl>` in a bordered 2-col grid.

Left: Provider (`Provider` + "reaction from Alpaca SIP completed bars") ·
Received (`ProviderReceivedAtUtc`) · Result first seen (`ResultFirstSeenAtUtc`, or
"Not yet observable") · Latest completed bar (`LatestCompletedBarAtUtc`) ·
Bar session (`LatestMarketSession`).

Right, from `PreviousEarnings`: Prior quarter (`ReportDateExchange`) · Prior EPS
(`EpsActual` vs `EpsEstimate`) · Prior surprise (`EpsSurprisePercent`) ·
Prior 1-day move (`OneDayPriceReactionPercent`) · Outcome then (`EpsOutcomeLabel`).

### Actions
Three controls at `min-height:28px; padding:4px 11px`, 11.5px:
`Inspect on desk` (link to `/TradeDesk?ticker=…`) ·
`Add to earnings wishlist` ·
`Show evidence` (scopes the rail to this ticker).

## Evidence rail (400px)

`padding:16px 18px 32px`, sticky, scrolls.
Header `NEWS & EVIDENCE` 15px/800/0.03em uppercase with a `newspaper` icon, caption
11.5px, count in a `.tag.tag-neutral`, all above a
`border-bottom:2px solid var(--color-text)`.
When scoped to a ticker, a `Show all evidence` secondary button appears.

Each item: `padding:11px 0 12px 11px;`
`border-bottom:1px solid var(--color-neutral-300);`
`border-left:3px solid <sentiment>` (same thresholds as the desk news list).

- `Tickers` 10.5px/700 left, `PublishedAtUtc` 10.5px tabular right
- `Headline` 12.5px/600
- `WhyItMatters` 11.5px `--color-neutral-800` — this field is the reason the rail is worth
  the space; always render it
- `Provider · Category` left and `sentiment ±N.NN · SentimentLabel` right, 10.5px

## State

| State | Default | Notes |
| --- | --- | --- |
| `scope` | `both` | Today / next / both |
| `session` | `all` | |
| `outcome` | `all` | |
| `onlyHeld`, `onlyNews` | false | |
| `minSurprise` | empty | absolute EPS surprise % |
| `sort` | `time` | |
| `alertLane` | none | intersects with the other filters, does not replace them |
| `newsScope` | null | ticker or null |

The countdown badge and the "reporting within 30 minutes" lane both depend on wall clock —
recompute on a 1s tick client-side; do not re-fetch for it.

Polling: keep the existing cadence. The monitor runs server-side whether or not the page
is open, so a refresh is a read, never a trigger.
