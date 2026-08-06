# 03 — Backtest Lab

Prototype: `prototypes/Backtest Lab.dc.html`
Replaces: `Pages/Backtests.cshtml` (configure) and `Pages/Job.cshtml` (running + results),
merged into one screen with three phases.
Design width: 1600px.

## Purpose

Select several strategies, run them against one wishlist universe, watch the run, and
compare portfolio-level results in a single view — with the promotion gates visible so a
profitable run is not mistaken for a promotable one.

## Phases

One screen, three states, switched by a segmented group in the page header
(`Configure · Running · Results`). In production the phase is derived from
`BacktestJobSnapshot.Status`:

| Status | Phase |
| --- | --- |
| no job selected | Configure |
| `queued`, `running`, `cancelling` | Running |
| `completed` | Results |
| `failed`, `cancelled` | Running, with the error panel shown |

Keep the existing 1.5s `?handler=Snapshot` poll and the reload-on-first-completion
behaviour.

## Page header
`display:flex; align-items:flex-end; justify-content:space-between; gap:20px;`
`padding:16px 20px 13px; border-bottom:2px solid var(--color-divider)`

Eyebrow `BACKTESTING RESEARCH` 10px/700/0.08em `--color-accent`;
`h1` **Strategy Lab** 30px/800/−0.02em; standfirst 13.5px `--color-neutral-800`,
`max-width:78ch`, `text-wrap:pretty`:

> Select strategies, run them against one wishlist universe, and compare portfolio-level
> results. A profitable run is not a promotion — the preregistered gates below decide that.

Top nav also carries a `.tag.tag-outline` reading
`RESEARCH DECISION · RETAIN_RESEARCH`, from the integrated research decision in
`docs/research/non-ml-strategy-research-implementation-status.md`.

## Shell
`display:grid; grid-template-columns:404px minmax(0,1fr); align-items:start`.
Left rail `border-right:2px solid var(--color-divider)`, sticky, scrolls at
`max-height:calc(100vh - 190px)`. Main scrolls independently.

## Left rail — configuration

Four blocks, each `padding:14px 18px` with
`border-bottom:2px solid var(--color-divider)`, heading 13px/800/0.06em uppercase.

### Universe (`layers` icon)
Wishlist `<select>` from `Model.Wishlists` labelled `Name · N active`. Below it the
resolved symbols as `.tag.tag-neutral` chips at 10.5px/700, `gap:4px`. Then, at 11.5px
`--color-neutral-700`:

> Point-in-time membership is required for a promotable run. This wishlist is a current
> list, so the run is marked research-only.

This is the single most important sentence on the screen. It is why the promotion gate
fails and it must not be dropped.

### Strategies
A bordered list, one `<label>` per strategy,
`grid-template-columns:18px minmax(0,1fr); gap:9px; padding:9px 11px;`
`border-top:1px solid var(--color-neutral-300)`. Selected rows get
`background:var(--color-accent-100)`.
Native `<input type="checkbox">` 16px with `accent-color:var(--color-accent)` — square,
because these are independent multi-select.

Per row: name + version 12.5px/700; `Direction · Timeframe / ExecutionTimeframe ·
SetupType` 10.5px; the audit line 10.5px tabular
(`Audit +N.NN% / DD N.NN% / N trades / win N.N% / avg hold X` from
`StrategyAuditSummary`); then a `.tag` reading `PROMOTED` (`.tag-accent`) or
`RESEARCH ONLY` (`.tag-neutral`).

Promoted strategies come from `configs/strategies`; research-only from
`configs/backtest/strategies`. Research-only must never be offered to paper or live —
label the distinction here so the operator learns it.

Footer line: `N selected · N work items · N research-only`, or, at zero,
"Select at least one strategy. A run with no strategy produces no evidence."

### Run parameters
2-col grid, `gap:10px`, `.input` at 32px. Fields map to `BacktestRunRequest`:
Run name · Lookback days (5–1825) · Starting capital · Account risk % ·
Max position notional % · Max positions · Market data (`use_cache` / `refresh`).

Footer, 11.5px:

> Scoring window N d plus 60 d warm-up. Completed-bar features, later-bar confirmation and
> next-bar execution are mandatory and not configurable.

The advanced strategy YAML editor and the optimisation runner from the current page stay
where they are — behind `<details>`. They are not part of this redesign.

### Run action
`.btn.btn-primary.btn-block` at `min-height:40px`, 13.5px, flush left, with a `play`
icon: `Run backtest · N strategies`. While running it becomes
`.btn.btn-secondary.btn-block` `Cancel run` with a `square` icon, posting to the existing
`OnPostCancel`.

Footer: "Generated run configs are audit artifacts. They are not hand-maintained ticker
lists."

## Running phase

### Run header
`padding:15px 20px 12px`. Run name 20px/800 with a status `.tag`
(`.tag-accent` while running, `.tag-neutral` when complete). Below it
`CurrentStage` with underscores replaced by spaces, plus `elapsed HH:MM:SS`, 12.5px
tabular. Right: `CompletedTickerCount / TotalTickerCount` at 26px/800 tabular over
`N.N% of work items`.

### Progress bar
`height:6px; background:var(--color-neutral-300); margin:0 20px`, fill
`background:var(--color-accent)`. No radius, no transition longer than 180ms.

### Phase rail — 4 cells
`grid-template-columns:repeat(4,minmax(0,1fr)); border:1px solid var(--color-divider);`
`margin:14px 20px 0`, each `padding:9px 12px`.

| # | Title | Detail | Stages (`CurrentStage`) |
| --- | --- | --- | --- |
| 1 | Setup | Config, strategies, universe | `loading_config`, `loading_strategies`, `finviz_candidates`, `resolving_universe` |
| 2 | Market data | Candles and indicators | `market_pipeline`, `preparing_ticker_contexts` |
| 3 | Evaluation | Strategy matrix | `strategy_group_ready`, `running_strategy_matrix`, `running_strategy_ticker`, `finished_strategy_ticker` |
| 4 | Results | Portfolio and audit | `building_results`, `writing_result`, `completed` |

States: active = `background:var(--color-accent-100)`, number and title
`--color-accent-800`, detail `--color-accent-900`. Complete =
`background:var(--color-neutral-100)`, text `--color-text`. Pending = transparent,
text `--color-neutral-500`. Reuse the `stagePhases` map already in `Job.cshtml`.

### Per-strategy cards — 2 columns
`grid-template-columns:repeat(2,minmax(0,1fr)); border:1px solid var(--color-divider);`
`margin:14px 20px 18px`, each `padding:10px 12px` with internal 1px rules.
From `BacktestStrategyRunGroup`:

- Name 12.5px/700 left, `CompletedTickerCount / TotalTickerCount` 11.5px tabular right
- `Status` + `· current TICKER` at 11px `--color-neutral-700`
- 4px progress bar, `--color-accent` while running, `--color-neutral-600` when complete
- `RecentEvents` reversed, up to 4, 10.5px tabular, each truncated to one line

### Activity
`Events` reversed, newest first, up to 12. Each
`padding:5px 0; border-bottom:1px solid var(--color-neutral-300)`, 11.5px tabular
`--color-neutral-800`. Empty state: "Waiting for the worker…".

### Error panel
When `ErrorMessage` is set: `padding:8px 10px;`
`border-left:3px solid var(--color-accent); background:var(--color-accent-100)`, 12px
`--color-accent-900`.

## Results phase

All five blocks below are one scrolling column, separated by
`border-bottom:2px solid var(--color-divider)`.

### 1. Summary — 4 cells, `grid-template-columns:1.5fr repeat(3,minmax(0,1fr))`
- **Winner** on `background:var(--color-accent-100)`: eyebrow `WINNER` 9.5px
  `--color-accent-800`, name 18px/800, then
  `+N.NN% return · +N.NNNN% daily · DD N.NN% · ranked on <metric>` 12.5px
  `--color-accent-900`. From `BacktestResult.Winner`.
- **Ending capital** `EndingCapital` 22px/800 tabular, `NetProfit` beneath with direction
  colour.
- **Accepted trades** `AcceptedTradeCount` over `N wins / N losses`.
- **Daily average** `AverageDailyReturnPct` over `TradingDayCount trading days`.

### 2. Equity curves + leaderboard
`grid-template-columns:minmax(0,1.25fr) minmax(0,1fr)`, 1px divider between.

**Equity curves** — inline SVG, `viewBox="0 0 640 210"`, `width:100%`,
`max-height:230px`, `role="img"` with an `aria-label`. Left gutter 42px for the axis,
22px below for month labels.
- Gridlines at max, midpoint, zero and min: 1px `--color-neutral-300`, with the **zero
  line at 1.5px `--color-neutral-600`**.
- Axis labels 9.5px `--color-neutral-700`, signed percentages, tabular.
- One `<path>` per strategy, `fill:none`. Stroke `--color-text` when the total return is
  positive, `--color-accent` when negative. The first series is 2.4px; the rest 1.6px.
  Series are distinguished by **dash pattern**, not hue:
  `none`, `5 3`, `2 3`, `8 3`, `1 3`, `6 2 2 2`, `3 5`.
- A 3px end-point circle per series.
- Legend below: a 16px dash swatch, name, and signed return.

**This needs a data series the API does not currently return — see `api-gaps.md`.**

**Leaderboard** — heading with a `trending-up` icon and a 170px metric `<select>`
(Total return % · Avg daily return % · Net profit · Max drawdown % · Win rate %).
Each row: rank 14px/800 in a 20px column (`--color-accent` for rank 1, else
`--color-neutral-600`), name 12.5px, value 13px tabular right, then a 4px bar indented
29px, `--color-accent` for rank 1 and `--color-neutral-500` otherwise, width proportional
to the largest absolute value. Drawdown sorts ascending; everything else descending.

### 3. Strategy × metric matrix
Full-width table. Columns: Strategy · Return · Daily avg · Net · Max DD · Trades ·
Win rate · Avg hold · Profit factor.

Cell fill encodes **rank on that metric**, not absolute value, so the table is readable
regardless of scale:

| Rank | Fill |
| --- | --- |
| 1st | `--color-accent-200`, value at 700 |
| 2nd | `--color-accent-100` |
| 3rd | transparent |
| 4th | `--color-neutral-200` |
| 5th+ | `--color-neutral-300` |

Negative return / net / daily values take `--color-accent-700` text on top of the fill.
The winner's row carries `background:var(--color-accent-100)` +
`box-shadow: inset 3px 0 0 var(--color-accent)`.
Rows are clickable and drive the analyzer and trades blocks below. Caption, 11px:
"Cell tint is the strategy's rank on that metric, best to worst. Click a row to drill into
its trades."

Sources: `BacktestStrategyResult` for return / daily / net / drawdown / trades / win rate.
Avg hold and profit factor are **gaps** — see `api-gaps.md`.

### 4. Promotion gates + analyzer
`grid-template-columns:minmax(0,1fr) minmax(0,1fr)`, 1px divider.

**Promotion gates** — bordered `<ul>`, each row `padding:7px 11px`, 12px:
13px glyph (`✓` / `✕`), 132px label, then the reason. Pass `--color-text`, fail
`--color-accent-700`. Seven gates, from `docs/operating-boundaries.md`:
Universe · Timing · Execution · Statistical · Cost stress · Concentration · Paper shadow.
Standfirst: "A missing gate fails closed. The integrated decision stays RETAIN_RESEARCH
until every one passes."

**These states are currently hardcoded in the prototype — they need a real evaluator.
See `api-gaps.md`.**

**Analyzer** — heading includes the drilled strategy name. A `<dl>` at 12.5px:
Evaluated bars · Candidates · Accepted · Per trading day · Best / worst day ·
Long / short. From `BacktestDiagnostic` (`EvaluatedBarCount`, `CandidateTradeCount`,
`AcceptedTradeCount`, `DailyPnl.*`, `DirectionPnl`).

Then **Top rejections**: up to four rows from `RejectionCounts`, each a label + count at
12px over a 4px `--color-neutral-600` bar scaled to the largest count. This turns the
current comma-joined string into something an operator can act on.

Then the suggestion: `padding:8px 10px;`
`border-left:3px solid var(--color-accent); background:var(--color-accent-100)`, 12px
`--color-accent-900`, from `Suggestions` joined.

### 5. Trades
Table for the drilled strategy: Ticker · Side · Entry · Exit · Exit reason · Hold · R ·
Net. From `CompletedTrades` (`EntryTimestamp`/`EntryPrice`, `ExitTimestamp`/`ExitPrice`,
`ExitReason`, `NetProfit`). R multiple is a **gap**. Net takes the direction colour.
Caption: `Most recent 10 of N accepted trades · one entry and one full exit per trade`.

## Configure phase — research snapshots

When no job is selected the main column shows the snapshot history: Run · Best strategy ·
Universe · Return · Max DD · Trades · Win rate · Decision. Decision is a `.tag`:
`.tag-accent` for `PAPER_SHADOW`, `.tag-neutral` otherwise. Values from
`Model.ResearchSnapshots`; `Universe` and `Decision` are **gaps**.

Closing note, always visible in this phase:

> Partial exits are not modelled. A backtest supports one entry and one full-position exit,
> so strategies that scale out will read differently here than in paper.

That limitation is stated in the repo README and belongs on the screen, not just in docs.
