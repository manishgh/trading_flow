# Review: OPRA options design (implemented) and Finviz EOD freeze / v4 plan (proposed) — 21 November 2026

Reviewer: Claude (Claude Code), at the user's request. Read-only: documents and code were
read, the operator archive folder was counted; nothing was built, run, changed or committed.

Documents reviewed:

- `docs/design-review/2026-11-20/alpaca-opra-options-sentiment-design.md` (identical on
  `main` and `ui/redesign-v2`), against the code on `main` at `d4b4688`.
- `docs/design-review/2026-11-21/finviz-eod-freeze-and-v4-plan.md`, against `main`,
  `C:\Users\manis\Downloads\Finviz_Elite_v4_Manual_Screens.csv` and
  `C:\Users\manis\Downloads\Swing_Strategies_v4.yaml`.

---

## Part 1 — OPRA options sentiment (implemented)

**Verdict: the display part is sound and matches the design; the strategy-veto part should
not stay as it is.** The calculator, Alpaca client, Desk endpoint and panel exist as
described, are read-only, and leave all strategy hashes unchanged. Two parts of the design
did not happen or cannot be used.

| # | Sev | Finding | Evidence | Why it matters | Suggested action |
|---|---|---|---|---|---|
| O1 | High | **The skew vetoes can never be tested, so they can never be legitimately used.** `max_long_put_skew` / `min_short_put_call_skew` are wired through about twelve production files, yet every backtest path refuses them and no strategy config uses them. | Refusals: `BacktestRunner.cs:1026`, `WorkflowRunConfiguration.cs:330`, `SignalStudyOutcomes.cs:77`. Wiring: `SimpleYamlReader`, `RunConfigWriter`, `BasicStrategyEvaluator` (×2), `StrategyDecisionKernel`, `StrategyDecisionRequestAssembler`, `LiveRunner.ProcessTicker`, `MobileAutomationService.EntryPreparation`, `StockStrategyEvidenceLoader`. `grep` of `configs/` finds no use. | Promotion requires a backtest first. A strategy with this veto cannot be backtested, so it can only reach paper by skipping that rule. If it did, paper would take different trades from anything tested, which breaks "one brain". Today the code is unused surface that every future change must keep working. | Remove the veto plumbing until a historical options dataset exists, keeping the display. If kept, mark it "cannot be enabled" in the catalog check, not only at run start. |
| O2 | High | **Historical options data was described as available, but it lacks what the vetoes need.** The design cites `/v1beta1/options/bars` and expired contracts (Feb 2024+). | Design §"Verified on the operator's paid Alpaca account". | Those are trade price bars. They carry no implied volatility, no Greeks and no bid/ask, so 25-delta skew cannot be read from them. It would have to be back-solved from trade prices of thinly traded strikes, which is a separate research project with its own error. | State this in the design so O1 is not mistaken for a small missing step. |
| O3 | Medium | **The fourth metric is never produced.** The design's implied-move ÷ ADR ratio needs `adr20Pct`, but no caller passes it. | All five callers of `GetSnapshotAsync` (Desk endpoint ×3, `StockStrategyEvidenceLoader`, `MobileAutomationService`, `LiveRunner`) omit `adr20Pct`, so `ImpliedToRealizedAdrRatio` is always empty. The UI branch then wrote its own unscaled ratio (finding M1 in `claude-code-design-review.md`). | A designed, tested feature shows nothing, and the replacement is mathematically wrong. | Pass the shared ADR value into the snapshot (or compute the ratio in one server service) and delete the branch copy. |
| O4 | Medium | **A fail-closed skew veto quietly becomes a "has liquid options" filter.** | `BasicStrategyEvaluator.cs:225` returns `options_skew_unavailable` when no skew is found. Skew needs liquid 25-delta puts and calls 20–45 days out, within 35% spread. | Most small and mid-cap stocks lack such wings, so enabling the veto removes them from the strategy's universe. That changes the tested population, not just the entry rule. | If the veto ever returns, say so in the config name or documentation and report how many candidates it removes. |
| O5 | Medium | **Maths limits** (also in `claude-code-design-review.md`, M5–M7): straddle move includes intrinsic value when no strike is at the price; skew thresholds are fixed numbers while index skew is normally positive; trading days ignore holidays; spot fallback from put-call parity ignores rates and dividends. | `OptionsSentimentCalculator.FindStraddleInWindow`, constants at lines 23–31, `CountWeekdaysExclusiveInclusive`. | Labels such as "Downside hedging" will be near-permanent for SPY/QQQ, and cheap stocks show inflated expected moves. | Remove intrinsic value; compare skew with each symbol's own history; use the exchange calendar. |
| O6 | Medium | **Tuning numbers are compiled constants.** Liquidity filter (35% spread, 0.80–1.20 moneyness), expiry windows (5–14, 20–45, 7–21, 30–60 days) and label thresholds (+0.06 / −0.04) are `const` fields. | `OptionsSentimentCalculator.cs:23–31`. | Changing any of them needs a code release, and the snapshot does not record which values produced it. | Put them in one options config and record the settings hash in each snapshot. |
| O7 | Low | **Client complexity.** 656 lines with 64 striped locks, a custom sweep past 512 entries, and follow-up page fetches for dense chains. | `AlpacaOptionsSnapshotClient.cs`. | Works, but `IMemoryCache` with a per-symbol shared task would remove most of the custom locking. | Simplify when next touched. |
| O8 | Low | **Call/put volume outside market hours is the previous session's.** | Uses the snapshot's daily volume. | The Desk shows it on Saturday without saying which day it covers. | Label "previous session" outside regular hours. |
| — | OK | Archive cost is modest today: 20 OPRA files, about 7 MB, on 20 November (about 350 KB per symbol snapshot). It grows with the number of symbols viewed. | `V:\trading_flow\data\raw\alpaca\2026-11-20`. | — | Keep an eye on it if the Catalysts cards (6 symbols per request) are used heavily. |

## Part 2 — Finviz EOD freeze and v4 plan (not implemented)

**Verdict: the freeze idea is right and most of the plan's facts check out, but the plan
should be split.** Phase A, the freeze, is a data-flow fix. Phases C–D add a new regime
model, a new entry-order type, a scoring system and eleven new strategies at once. Those
need their own designs and approvals.

### What I verified as correct

- The CSV has 22 rows, **one** distinct `broad_url` and **eight** distinct `focus_url`
  queries. Ten rows use `ta_changeopen_u/d`. The focus screens are stricter than the v4
  hard rules exactly as stated (`ta_perf_1w10o`, `ta_perf_1wdown`, `ta_sma50_pa`).
- `ScreenerSyncService.cs:99` caps a screen at 250 rows.
- Source snapshots expire after 180 seconds by default. The constant is
  `DiscoveryRuntimeConfig.DefaultSourceExpirySeconds` in
  `TradingFlow.Domain/Backtesting/BacktestRunConfig.cs:55`, not `UniverseWorkflowAdapters.cs` as cited.
- `FinvizScreenCaptureScheduler.cs:221` already captures at exchange close + 15 minutes
  (13:15 on early-close days).
- The morning run re-queries sources. With `FreshDiscoveries` off it can only **remove** names
  from the nightly list (intersection at `PreparationService.cs:268`). With it on, the live
  screen replaces the list.
- `IndicatorEngine` already has RSI(2) and Bollinger(20, 2).
- Path errors: `SignalGenerator.cs` is in `Engine/Strategies`, not `Engine/Signals`; links use `file:///c:/…`.

### Where the plan changes the flow you described

From the plan's own summary, your flow was: freeze the scan after close, keep the raw
exports, verify on completed daily bars, and overlay live data without overwriting
yesterday's setup. The plan departs from that in four places. Each needs your explicit yes.

1. **The Finviz focus screens stop deciding anything.** All v4 rules run on the ~1,400-stock
   broad list; focus hits become tags only.
2. **Live additions come back.** For S3 and the intraday set, same-day news and pre-market
   gap movers are added ("unioned") to the frozen list. That is a morning re-screen, though
   only additive.
3. **The Desk and News group menu loses the built-in Finviz presets** (Channel Up, Wedge,
   Head & Shoulders and so on), replaced by "Frozen EOD" and "Finviz v4 screens".
4. **Phases C–D change trading rules**: a soft regime multiplier, VIX > 26, stop-limit entries
   with caps, a daily 1% drawdown halt, one position per symbol across modules, and a quality score.

### Findings

| # | Sev | Finding | Why it matters | Suggested change |
|---|---|---|---|---|
| V1 | High | **If every rule runs on the broad list, only one Finviz query is needed, and Finviz may not be needed at all.** The broad filter (US, price > $10, average volume > 1M, mid-cap and above) can be computed from Alpaca daily bars plus a market-cap source. Phase B already proposes exactly that as a point-in-time screen for backtests. | Using one rule-based screen for both backtests and paper gives a single universe definition, which is the project's "one brain" goal. It also removes the Finviz rate-limit and 250-row work from the critical path. | Build Phase B's screen first and use it for paper as well. Keep the 16:15 Finviz capture (one broad query, plus focus queries only if you want the tags) as an audit comparison. |
| V2 | High | **"Verify at 16:15 on the completed daily bar" cannot happen at 16:15.** The code treats a daily bar as complete only at its timestamp + 1 day. The Desk's own note says Alpaca's daily aggregate includes extended hours (`watch-desk.js:480`), while `Swing_Strategies_v4.yaml` says `bar: 1D completed US regular session`. | Indicators frozen at 16:15 would use a bar that is still changing in after-hours trading, or would mix after-hours highs, lows and volume into "regular session" rules. | Build regular-session daily bars from regular-hours intraday bars (or the official close), and state when verification runs (for example 20:15 ET or 04:00 ET next day). Finviz's 16:15 values stay discovery only. |
| V3 | High | **Phases C–D are several strategy projects in one checkpoint plan.** They add a second regime model (soft 1.0 / 0.75 / 0.5 with `market_context_hard_gate: false`) next to v1's hard gates (VIX 18 / 22), new stop-limit entry rules, a quality score, 7 swing and 4 intraday strategies. | Two regime systems will disagree on the same day, as the regime header already shows (P3 in the code review). Testing eleven strategies at once raises the chance that one looks good by luck. The operating boundaries require frozen rules and an untouched test period before results count. | Split into separate proposals: (a) one configurable regime component that can express both v1 and v4; (b) stop-limit execution in the shared simulator and paper path, with parity tests; (c) strategies a few at a time, each frozen before its first run. Confirm Alpaca accepts the needed entry type inside a bracket order before designing around it. |
| V4 | Medium | **`quality_score = passed ÷ measured` rewards missing data.** Unknown checks drop out of the denominator. | A stock with one measured check that passes scores 100%, above a stock with 7 of 8 passing. | Use passed ÷ total, with unknowns counted, and show the unknown count next to it. |
| V5 | Medium | **Removing the Finviz presets from the menu** affects things AGENTS.md records as deliberately kept ("genuine Finviz presets" survived the last reset). | It is your operator view, so it needs a separate yes. | Group them under a collapsed "Finviz signals" heading instead (G11 in the UI handoff note). |
| V6 | Medium | **Nightly cost is not estimated.** "< 50 ms on cached bars" assumes the cache exists. It needs 260+ daily sessions for ~1,400 symbols, plus 20 days of 5-minute bars for time-slot RVOL (about 1,400 × 20 × 78 ≈ 2.2 million bars). | Collection time, Alpaca request volume and disk use decide whether this finishes before the next session. | Add a sizing estimate. Store through `ICandleStore`, and collect incrementally (one new day per night) rather than re-fetching. |
| V7 | Medium | **The "10 calls per rolling 5 seconds" Finviz Elite limit is not sourced.** | A shared limiter built on a wrong number either throttles needlessly or still gets HTTP 429. `FinvizClient` already honours `Retry-After`. | Cite Finviz's documentation or measured 429 responses first. With V1, one call per evening makes this moot. |
| V8 | Medium | **Freeze lifetime edge cases are missing.** "Valid through next session 15:30 ET" does not cover: Friday → Monday and holidays; a failed next-evening capture; stock splits or symbol changes overnight (frozen trigger prices become wrong); trading halts; earnings reported after the freeze for non-S3 strategies. | Each case can turn a frozen setup into an order at a wrong price or on stale information. | Add rules: expire at the next session's cut-off by exchange calendar; re-check corporate actions and earnings at pre-market; mark halted names; never carry a freeze past one session. |
| V9 | Medium | **Manual CSV import needs more rules**: required columns, what happens when both an automatic capture and a manual file exist for the same session, and duplicate uploads. | Two sources for one session must not silently merge. | Automatic capture wins and the manual file is stored as a diagnostic, unless you mark the automatic one failed. Validate columns against the Finviz export header. |
| V10 | Low | **Same-day additions (change 2) must stay a separate, labelled source.** | If they mix into the frozen set, the freeze no longer protects yesterday's setups from today's data. | A distinct source "Same-day catalysts (observed HH:MM)" in the menu and audit, never written into the frozen snapshot. |
| V11 | — | **Branch question (decision 2): resolved.** The user asked for the UI work to be on its own branch (`ui/redesign-v2` in `C:\project\trading_flow-ui2`), so the separate worktree is intended. Where Phase A lands is still decision 2 in the plan. | — | — |

### Recommended order

1. Phase B screen (V1) and regular-session daily bars (V2), so paper and backtests share
   one universe.
2. Phase A reduced to: freeze, verify after the bar is final, keep for exactly one session,
   overlay live data, add manual import (V8–V10). One Finviz call as an audit comparison.
3. Separate proposals for the regime component, stop-limit execution and each strategy
   group (V3, V4).

---

## Side finding — operator raw archive volume (not part of either design)

While sizing the OPRA archive, the operator folder `V:\trading_flow\data\raw\alpaca` showed:

| Day | broker-account-snapshot | news-rest-page | open positions / open orders | OPRA |
|---|---|---|---|---|
| Fri 20 Nov | 73,023 | 46,451 | 5,677 each | 20 |
| Sat 21 Nov (market closed, to ~21:10 UTC) | 65,733 | 33,711 | 5,093 each | 0 |

Saturday's account snapshots run at about 3,100 per hour (≈ 0.9 per second), each with a
manifest file, so about 220,000 files were written that day. These are provider receipts, which
AGENTS.md classes as core data that cannot be deleted without dataset-specific approval, so
the folder only grows. The `/v2/account` request is archived in
`AlpacaBrokerClient.GetAccountSnapshotAsync`. It has about 15 callers (reconciliation,
entry gates, order dispatch, loss review, live runner). The browser account strip polls only
every 30 s, so the rate comes from a server loop, not the page. **The calling loop was not
identified in this review.** Next step: count snapshots per minute against the operator log
to find the caller, then decide whether repeated identical account reads should be archived
at all.
