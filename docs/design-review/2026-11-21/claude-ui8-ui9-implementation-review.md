# Review of UI-8 and UI-9 against the Claude wireframe — 21 November 2026

Reviewer: Claude (Claude Code), at the user's request. Commits reviewed on `ui/redesign-v2`:
`e16c7db` (UI-8) and `b98c08e` (UI-9), compared with
[`desk-layout-wireframe.png`](desk-layout-wireframe.png),
[`claude-ui-review-handoff.md`](claude-ui-review-handoff.md) and
[`claude-code-design-review.md`](claude-code-design-review.md). Measured on the preview
`http://127.0.0.1:10089` at 1440×900 (site rebuilt 23:13), with CSS pixel positions read in
the browser. Nothing was changed.

**Verdict: a large improvement, and the layout follows the wireframe closely.** The columns
measure 301 / 556 / 301 px (26 / 48 / 26%, exactly as drawn). The stock table moved from
814 px down the page to 307 px, and rows went from 98 px to 35 px. The tall dot column on the
chart is gone. Gemini changed some details from the wireframe, mostly for the better (news
in the right column). Four layout defects remain, and the maths and data-path items from
the code review are still open.

## 1. Wireframe vs implementation

| Area | Wireframe | Implemented | Assessment |
|---|---|---|---|
| Status line | One line: account state, market, SPY/QQQ gate, VIX, options stress, sectors, preparation | All of these chips present, plus "Source freshness" and "Checked …"; wraps to **two lines** (59 px) under an 88 px top bar | Good content. Two lines push the table to 307 px (target ≤ 260). Move "Checked …" and "Source freshness" into a tooltip to fit one line. |
| Page title | Removed on Desk | Removed on Desk; **still present** on Strategy Lab and News (label, heading, subtitle) | Apply the same on the other workspaces for consistency. |
| Toolbar | Group, strategy, find, view counts, fewer columns | Same, plus Sort, + Stock, + Screen, Groups link; view tabs moved into the toolbar | Good. "Find" placeholder ("Symbol or compa…") and Sort ("Symbol A-…") are **clipped**. |
| Stock list | 26%, ~34 px rows; Stock, Mid, Day %, News, Status | 26%, 35 px rows; Stock, Mid · Day %, News, RVOL · ATR; Strategy column appears once a strategy is chosen | Good. "RVOL · ATR" header is truncated. |
| Chart column | Price header, interval buttons, chart fills height, one indicator line, one options line | Price header with info icon, interval buttons, Buy/Sell, chart 420 px tall, indicator strip, options card | Close, with defects L1–L4 below. |
| Right column | Why / why not checklist, risk ticket at bottom | Why / why not checklist, **stock news list**, group & catalyst tools; no risk summary | News here uses the space well; a better choice than the wireframe. A one-line risk summary is missing (L5). |
| Strategy Lab | Long and short grouped, one "research only" note | Two columns (long left, short right), single "All listed strategies are research only." note | Matches. |
| News | One line per story | One line: sentiment chip, ticker, headline, time | Matches. "Broad market (MA…" ticker label is truncated. |

## 2. Handoff items status

Done: D4 regime strip offset, D5 labelled SPY/QQQ values, D6 tall marker column (root cause
was many news markers stacked on one candle, now grouped to one marker per candle; MU now
scales 600–1,200 instead of to 1,400), D7 resistance no longer red, D8 chart fills its
column, G1 per-row "Market closed" removed, G2 Desk titles, G4 options stress chip, G5 blank
news at 0/0, G6 Strategy column hidden until chosen, G7 price shown once, G8 info icon, G9
preparation chip, G11 grouped source menu (Overview / My groups / Saved screens / Finviz
signals / Prepared), G12 Lab note and columns, G13 news rows, G14 footer text hidden, G15
"Fewer columns" / "All columns". Layout line joins in `_Layout.cshtml` fixed. Global
`button { min-height: 40px }` now scoped to `.btn:not(.btn-sm)`.

Not done: G3 on News (the SPY/QQQ cards still repeat the four numbers in a sentence), G10
not verified on screen, D9 Earnings empty state not checked.

## 3. Layout defects found

| # | Sev | Defect | Measured | Fix |
|---|---|---|---|---|
| L1 | High | **Buy and Sell sit on top of the interval buttons.** Buy occupies x 765–807, y 317–343 and Sell y 348–374; the "1d" button is at x 783–813, y 331–355. A click at the centre of Buy lands on the interval group (`document.elementFromPoint` returns `.watch-interval-quick`). | Chart header, `#watch-buy`, `#watch-sell`, `.watch-interval-btn`. | Put Buy/Sell in their own flex item at the right of the price line (or under the price), never in the same row as the interval group; check at 1200, 1440 and 1920 px. |
| L2 | Medium | **Hover readout and SMA chips cover the top of the chart.** `.stock-chart-readout` spans y 390–468 over a plot that starts at 384, hiding about 80 px of candles. | `stock-chart-readout`, inline SMA chip buttons. | One-line readout (date · O H L C · volume) as a thin overlay; move SMA chips to the indicator strip, which already shows the same values. |
| L3 | Medium | **Same numbers, different formats.** The chips show "SMA 20 786.649 USD" and "SMA 200 772.3604 USD"; the strip below shows "$786.65"; the readout shows "Low $585.2526". | Chart readout vs `.watch-chart-indicators-strip`. | Two decimals and a `$` sign everywhere (more only for prices under $1). |
| L4 | Medium | **Indicator strip overlaps the chart bottom, and the options card is below the screen.** Strip y 792–845 vs chart stage ending at 804; options card y 867–992 on a 900 px screen; page height 1,079 px. | `.watch-chart-indicators-strip`, `#desk-options-selected`. | Reserve the strip's height in the grid (no overlap). Shorten the options card to one line as in the wireframe, or reduce the chart height so all three fit in 900 px. |
| L5 | Low | **No risk summary on the Desk.** The wireframe ended the right column with "Max loss · stop distance · shares · Preview bracket order". | Right column. | When a strategy is selected and the server plan exists, show its risk figures in one line above the news list (figures from the server, see code review M8). |

## 4. Code points in UI-8 / UI-9

| # | Sev | Finding | Fix |
|---|---|---|---|
| K1 | Medium | **The "3-file CSS merge" split the new file instead of merging the old ones.** `ui2-foundation.css` became `tokens.css` (86), `components.css` (232) and `workspaces.css` (772 lines), and all ten older global stylesheets still load before them (`site.css` … `desk-theme.css`). Overrides grew: `.workspace-app` selectors in `workspaces.css` went from 47 to 109, and `!important` from 2 to 6. | Move the rules that are still used out of the older files into the three new ones, delete the old files, then drop the `.workspace-app` prefixes. Do one workspace at a time with before/after screenshots. |
| K2 | Low | `watch-desk.js` grew from 1,160 to 1,343 lines. | Split as suggested in the code review (S2). |
| K3 | Low | The Desk strategy picker shows a recorded run whose exact version is no longer in the catalog when any enabled version of the same strategy exists, and takes its direction from that other version. | `IsPickerEligibleRecordedStrategy` in `DeskStrategyAssessmentService.cs`: label such rows "older version" and take direction from the run's own recorded settings. |
| K4 | — | Still open from the code review: M1 (News shows SPY "▲ 2.21×" from the unscaled ratio), M2 ("Day %" after hours), P1–P3 (direct provider fetches with archiving from the header), R4 (gate wording with "(>22)" still in `watch-desk.js`), and no logging in the new server code. | As in `claude-code-design-review.md`. |

## 5. Scope note — strategy files changed in a UI commit

`b98c08e` also removes 103 lines from `configs/strategy-catalog.json` and moves the
opening-range breakout (`bt-v3`, `bt-v4`, `bt-v5`) and ETF (`trend-10m`, `rotation-12-1`,
`buy-and-hold-control`) strategy and profile files into
`src/TradingFlow.Tests/Fixtures/RetiredStrategies/`. The catalog now holds only the ten
swing artifacts. `AGENTS.md` still lists opening-range breakout 1.2.0 / 2.0.0 / 2.1.0 and two
ETF strategies as operationally enabled. No retirement ledger file
(`docs/research/retired-strategy-artifacts-*.md`) was added in that commit. Strategies are
out of this review's scope; if this retirement was intended, record it in a retirement
ledger and update `AGENTS.md` ("Active Configs") so the two agree.

## 6. Implemented on 21 November 2026 (uncommitted on `ui/redesign-v2`)

The user asked to implement what was missed and keep what Gemini did better.

**Kept from Gemini (better than the wireframe):** stock news in the right column; the
server-only risk summary (`#watch-ticket-plan-summary`: 1R budget, entry, stop, target),
which appears once a strategy is chosen, so no share count is computed in the browser.

**Implemented:**

| Item | Change | Measured at 1440×900 after the change |
|---|---|---|
| L1 | Name/price group no longer shrinks under its content (`min-width: 0` removed); Buy/Sell kept on one row | Buy at its own position; a click lands on `#watch-buy`; interval buttons on the second header line |
| L2 | One-line on-chart readout; moving-average chips hidden inside it on the Desk (the pills below show the same values and toggle the highlight) | Readout 30 px tall (was about 80 px) |
| L3 | One price format: `$` and two decimals, up to four only below $1 (`stock-chart.js`, `stock-chart-indicators.js`) | Readout "$665.46 / $585.25"; pills "$786.65" |
| L4 | Grid height `calc(100vh - 290px)` (was `- 205px`, which ignored the real 280 px offset); chart height `clamp(320px, 100vh - 525px, 560px)`; options line flattened | Columns 280–890 px; chart 375 px; options line 820–881 px; nothing below the screen |
| Strip | Chip words shortened ("▼ 50-day: shorts"; tooltip explains the standard rule and that each strategy uses its own settings); "Checked …" moved into tooltips; "Source freshness" → "Sources"; chip spacing trimmed | 33 px, one line (was 59 px) |
| Toolbar | Find placeholder "Symbol or name"; Sort option "A–Z"; compact column widths rebalanced when no strategy is selected (18 / 41 / 16 / 25%) | No clipped labels; price cell 116 px fits "$24.20 ▲ +4.00%" |
| G2 | Lab, News and Earnings headings visually hidden (eyebrow and subtitle removed) | — |
| G3 | News options cards: summary sentence moved into the card tooltip | — |
| M1 | `CatalystOptionsAdrService.ComputeDailyImpliedMoveToAdrRatio` uses the shared `OptionsSentimentCalculator.WithAdr20Pct` (move ÷ √days ÷ ADR%); display bands recalibrated to 0.75× / 1.25× with a plain-language tooltip ("display guide only, not a trading rule; about 0.5× is an ordinary day") | SPY 0.99×, QQQ 0.88× (was "▲ 2.21×") |
| M2 | Outside the regular session the column reads "Mid · vs prev close"; tooltips name the quote (after-hours, pre-market, overnight, market closed, or session unknown) and the close date, and say the figure includes every move since that close | On Saturday: "Latest quote (market closed) vs 2026-11-20 close $23.27 …" |

Strategy retirement confirmed by the user: `AGENTS.md` "Active Configs" now lists the ten swing
artifacts; ledger `docs/research/retired-strategy-artifacts-2026-11-21.md` records the six retired
identities, file hashes and the four removed run profiles.

**Verification:** `dotnet build` of the test project (includes the web app): 0 warnings, 0 errors.
Focused tests (`Ui2RedesignTests`, `WorkspacePulseTests`, `KeptStrategyIdentityTests`,
`NewsCategoryUiTests`, `OptionsSentimentTests`, `ChartIndicatorResourceTests`, `ChartEndpointTests`,
`StrategyArtifactCatalogTests`, `StockStrategyAssessmentTests`, `DeskTicketPlanTests`): 113 passed,
0 failed. `node --check` on all changed scripts. Release build installed on the preview
(`C:\project\tradingflow-site-ui2`, port 10089, previous build kept as `bin-old`) and checked in the
browser at 1440×900. The full test suite was not run (component change, not a release).

**Independent reviews:** plan/governance/financial-accuracy review — ACCEPT, with three label fixes
(ratio bands, regime chip wording, "vs prev close"), all applied. Code/design review — ACCEPT, with
four fixes: ineffective CSS rule removed and comment corrected; unknown/overnight session wording;
browser "Market" label reverted so browser and server news rows match; formatter comment corrected.
VIX chip tooltip now carries the check time; the chart-header tooltip keeps "Prev close" when the
midpoint is missing.

**Still open (not in this change):**
- K1 stylesheet merge (ten older stylesheets still load before the three new ones).
- Ratio bands are a rule of thumb, not calibrated on history; moving the classification to the server (R5).
- Code-review items P1–P3 (direct provider fetches and archived receipts from the status strip), R4 (gate wording in JavaScript), no logging in the new server code.
- Text-only tests: behaviour of `formatRowDayChange` / `isRegularSession` is not tested in a browser test.
- Preview-only error, present since 19:42 UTC before these changes and absent from the operator site:
  `POST /api/v1/desk/strategy-assessments` returns HTTP 500 with
  "UNIQUE constraint failed: Jobs.JobType, Jobs.RunName" (764 occurrences in the preview log).
