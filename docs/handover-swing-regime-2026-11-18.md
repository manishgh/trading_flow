# Handover — swing strategies, market regime and VIX (state at 19 November 2026, ~21:35 UTC)

For the next session. Everything below is committed on local `main` (not pushed; no GitHub sync without the user asking).
The user's own uncommitted edit `docs/research/tradingflow-audit-2026-11-14.md` must stay untouched and unstaged.

**Rule (user, 18 Nov):** after every checkpoint, update this file — tick what is done, keep what remains — and commit it.

## Goal

**The user can run every long and short swing strategy from the web app** (Backtests page), without command-line steps.
Each item below still needs written design approval before building (AGENTS.md), two independent reviewers, the
affected tests, and installation on `C:\project\tradingflow-site`.

## Checklist

Market and stock filters
- [x] SPY rising-average regime rule (checkpoint 1, `d252620`)
- [x] ADR% screen rule, user's definition (checkpoint 1)
- [x] QQQ for Nasdaq-listed stocks (checkpoint 2, `ef538c9`)
- [x] VIX sizing from FRED, previous close: full < 18, half 18–22, no longs > 22 (checkpoint 3, `ede0ce2`)
- [x] Strategy-owned 20-session ADR% & average daily dollar-volume filter (`min_average_range_pct`, `min_average_dollar_volume`, matching `DailyScreen` across backtests & paper/Desk — Batch 1)
- [x] Reversed regime for shorts (SPY/QQQ below falling 50-day, VIX short sizing, borrow fee, SEC Rule 201 — Checkpoint 7, Batch 3)
- [ ] Sector-fund rank labels on trades (report only) — assigned to the first run's report

Long strategies
- [x] Bollinger squeeze breakout v1 built (registered, disabled until checkpoint 3)
- [x] Enable the squeeze in `configs/strategy-catalog.json` after checkpoint 3 (`349ab69`)
- [x] Swing ORB on news (`technical.news-orb-swing@1.0.0`) — Option B (decide after close, buy next open; 30m 5m ORB/RVOL/VWAP, +5m conservative Tier-1 catalyst window, 0.75 ATR gap, SPY/QQQ/VIX gate, Checkpoint 4 — enabled for research backtests)
- [x] 21-EMA trend pullback Long (`technical.ema21-trend-pullback@1.0.0` ATR-14 control + `1.1.0` V2-01 MTR-14 treatment, Checkpoint 5 — enabled for research backtests)
- [x] Rubber-band reclaim Long (`technical.rubber-band-reclaim@1.0.0` — Archetype #4: combined lower-Bollinger + RSI(2) < 10 stretch, 48h fresh-news veto, rising SMA-50, SPY/QQQ/VIX gate, 1.0 ATR / 2.5R / 10d exit, Checkpoint 6 — enabled for research backtests)
- [ ] Run the 6-month diagnostic (May–Oct 2026) with the full `.log` (Checkpoint 10)

Short strategies (each uses Checkpoint 7 reversed regime, frozen borrow fee, and SEC Rule 201 — Checkpoint 8, Batch 3)
- [x] Squeeze breakdown (`technical.bollinger-squeeze-breakdown@1.0.0` — enabled for research backtests)
- [x] Swing ORB gap-down on bad news (`technical.news-orb-breakdown@1.0.0` — enabled for research backtests)
- [x] 21-EMA rejection short (`technical.ema21-rejection-short@1.0.0` + V2-01 `1.1.0` MTR-14 — enabled for research backtests)
- [x] Parabolic exhaustion short (`technical.parabolic-exhaustion-short@1.0.0` — Archetype #4 short mirror, enabled for research backtests)
- [x] Engine short setups for these (`bollinger_squeeze_breakdown`, `news_orb_breakdown`, `ema21_rejection_breakdown`, `parabolic_shooting_star_breakdown`)

Running it from the web app
- [x] Backtests page can select published research screens, strategies, dates, capital and risk (existing)
- [x] Browser tests use a test-owned catalog (`532d8ac`)
- [x] Web page to build a research screen (Checkpoint 9: collapsible `_ResearchScreensPanel.cshtml` on Backtests backed by durable `research_evidence` jobs and shared `ResearchDailyScreenPublisher`, plus `Minimum ADR%` in `Daily eligibility filters`)
- [x] Web page to collect five-minute bars for a screen (Checkpoint 9: two-step Estimate -> Confirm & Collect flow via shared `IntradayBarsCollectionPlanner` with `PriorSessions = 21`, held sessions, and full-RTH `09:30-16:00` catalog subtraction)
- [x] Each strategy enabled in the catalog once built and reviewed (all 10 swing artifacts — 4 Long + 4 Short + V2-01 Long & Short — enabled for research backtests only; paper/live remain unauthorized)

Housekeeping
- [x] Handover written (`8e75ab0`) and kept as this checklist
- [ ] Delete session scratch files at the end of the session (list under Housekeeping below)

## What the user wants (in their words, condensed)

- Swing trading only ("leave intraday now"); index and sector funds only to **judge the trend**, not to trade.
- Rule-based scanner (Stage 1) in TradingFlow; ML (XGBoost, Stage 2) stays in the separate Market Predictor project.
- Strategy families, long and short: squeeze breakout/breakdown, 21-EMA pullback/rejection, swing ORB on news.
  Mean reversion (Connors RSI-2, snapback) was already **retired 13 Nov 2026** — only back with a new, stated hypothesis.
- ADR% definition (user): mean(high − low) over 20 sessions ÷ latest close × 100; trade only ADR% ≥ 2.5 %.
- VIX (FRED `VIXCLS`, always the **previous trading day's close**): < 18 full risk, 18–22 half risk, > 22 no new longs.
- First economic run: **6-month diagnostic, about 1 May – 30 Oct 2026**, three versions (no gate; SPY+QQQ; SPY+QQQ+VIX),
  full `.log` file of the whole run. The user accepted that these months are then **spent** as a test period.
- "Finish all, then write a handover" — this file.

## Decisions and designs (read these first)

| Document | Status |
|---|---|
| `docs/design-review/2026-11-19/swing-8-strategies-and-ui-design.md` | approved 19 Nov 2026: all 4 long + 4 short archetypes, strategy-owned ADR%/DV filter, Checkpoints 4–10 (Checkpoints 4–9 done; Checkpoint 10 next) |
| `docs/design-review/2026-11-18/regime-gate-and-bollinger-squeeze.md` | approved (option A); checkpoints 1–3 done, squeeze enabled for research backtests |
| `docs/design-review/2026-11-18/swing-orb-and-vix-gate-proposal.md` | swing ORB approved with option B ("all approved"); VIX part superseded by the user's choice above |
| `docs/design-review/2026-11-18/experiment-v2-01-median-true-range.md` | registered experiment, host = 21-EMA pullback (not the squeeze) |
| `docs/operating-boundaries.md`, `AGENTS.md` | binding rules (design approval, two reviewers, install after review, data deletion rules) |

## Done (commits)

- Checkpoint 9 (Web UI Research Screen Builder, Two-Step 5-Minute Bar Collector & `Minimum ADR%` Daily Eligibility, 19 Nov):
  extracted shared `ResearchDailyScreenPublisher` (atomic staging-folder promotion + cleanup, async progress, core-data
  non-overwrite guard), `DailyRankingOutput`, `IntradayBarsCollectionPlanner` (`PriorSessions = 21`, held non-eligible
  sessions, $O(\text{partitions})$ session-sharded `09:30-16:00` `5m` catalog subtraction), and
  `EvidenceDatasetCollectionRunner` into `TradingFlow.Data` (thin CLI adapters, `TradingFlow.Web` compiler-isolated from
  `TradingFlow.Research.*`); wired `ResearchEvidenceJobService` (`research_evidence` durable job handler, zero new
  `IHostedService`), `/api/v1/backtests/research-screens/*` endpoints, `_ResearchScreensPanel.cshtml` +
  `research-screens.js` (`window.tradingFlowFetch` CSRF + `pagehide` abort, `#StockSource` refresh), and Option 1
  `EligibilityMinAverageRangePct` across `Backtests.cshtml`, `run-workflow.js`, `BacktestEligibilityRules`, and
  `BacktestEligibility` (`0 warnings / 0 errors` across 18 projects, `85/85` focused tests green, both independent
  reviewers ACCEPT in Round 2).
- `1905584` Batch 3 — Checkpoints 7 & 8 (Shared Short Foundation + All 4 Short Mirror Strategies + V2-01 Short, 19 Nov): added
  reversed SPY/QQQ falling-SMA regime (`price_below_falling_sma`), short VIX sizing (`vix_no_new_shorts_below: 18` /
  `vix_short_full_risk_above: 22`), annualized closing-fill short borrow fee (`ShortBorrowCostCalculator`,
  `short_borrow_apr_pct`) reconciled across `BacktestTrade`, `NativeDailySessionLedger`, `BacktestOpportunityShadowReplay`,
  and sampled account loss, live/paper/Desk `EasyToBorrow` enforcement, SEC Rule 201 on bar D and D+1 open, short
  `DailyRthFiveMinuteV1` + `KeptGapAsTradedTimingPolicy` accounting & verified `MonthlyRunResultWriter` output, all 4 short
  engine setups (`bollinger_squeeze_breakdown`, `news_orb_breakdown`, `ema21_rejection_breakdown`,
  `parabolic_shooting_star_breakdown`), and enabled all 5 short recipes (`technical.bollinger-squeeze-breakdown@1.0.0`,
  `technical.news-orb-breakdown@1.0.0`, `technical.ema21-rejection-short@1.0.0`, `technical.ema21-rejection-short@1.1.0`,
  `technical.parabolic-exhaustion-short@1.0.0`) in `configs/strategy-catalog.json` for research backtests only
  (0 warnings/0 errors build, 185/185 Round 1 + 116/116 Round 2 focused tests green, both independent reviewers ACCEPT).
  All 8 Swing Strategies (4 Long + 4 Short + V2-01 Long & Short) are now complete.
- `a058bb5` Batch 2 — Checkpoint 4 (`technical.news-orb-swing@1.0.0`, Option B, 19 Nov): added `DailyOpeningRangeSummary`
  (`O(log M)` backward scan, exact timestamp deduplication, early-close `13:00` NY support, 30m ORB/RVOL/VWAP),
  `TierOneCatalystEvaluator` (`+5m` conservative receipt delay preserved separately from actual `firstReceivedAtUtc`,
  multi-story Tier-1 + FinBERT negative veto, day-D regular-close cutoff & `10:00 NY_D - 18h` AMC lookback),
  `MinGapUpAtr`, `ResearchScreenSources` eligible-list `5m` binding + pre-flight `5m` & strict-news guards, and enabled
  `technical.news-orb-swing@1.0.0` for research backtests (0 warnings/0 errors build, 148/148 + 22/22 tests green, both
  independent reviewers ACCEPT). All 4 Long Swing Strategies are now complete.
- `c75524f` Batch 1 — Checkpoints 5 & 6 + Strategy-Owned ADR%/Liquidity Filter (19 Nov): added `Ema21`, `Mtr14`, `MaxTr14`
  (`swing-daily-v2-as-traded-research-v1`, `derivedIndicatorProfileVersion: "2.5.0"`), strategy-owned 20-session
  `min_average_range_pct` / `min_average_dollar_volume` matching `DailyScreen` across backtest & paper/Desk,
  `sma50_rising_lookback_bars`, `setup_type: ema21_pullback_reclaim`, `exit_rules.volatility_unit` (`atr14` | `mtr14`),
  `require_combined_bollinger_and_rsi2_stretch`, and enabled `technical.ema21-trend-pullback@1.0.0`,
  `technical.ema21-trend-pullback@1.1.0` (V2-01 MTR-14, `derivedFromIdentity` bound to `1.0.0`), and
  `technical.rubber-band-reclaim@1.0.0` in `configs/strategy-catalog.json` for research backtests only (0 warnings/0 errors
  build, 163/163 focused tests green, both independent reviewers ACCEPT).
- `ede0ce2` checkpoint 3 (19 Nov): config-driven FRED `VIXCLS` previous-session sizing (`vix_series`, `vix_full_risk_below`,
  `vix_no_new_longs_above`, `vix_reduced_risk_scale`) via one shared `OrderPlanningRiskLimits.EntryRiskScale` multiplier
  across daily, event, intraday, opportunity shadow-replay, paper, mobile, and Desk paths. Backtests read SHA-256-verified
  `fred-vixcls.csv` and `fred-vixcls-receipt.json` published with `research-daily-screen --fred-vix`, enforced pre-flight
  before trial registration and recorded in `ResearchScreenReference`, trial-registry datasets (`fred-vixcls:<sha>`), and
  `{runName}-daily-eligibility.json`. Authoritative exchange session calendars filter non-trading-day bars and fail closed
  on stale benchmarks (`market_regime_off_on_entry_day`), backed by $O(\log N)$ pre-indexed lookups; paper/Desk share
  `RegimeGateService.Evaluate` and `FredVixCloseSource` (15-min cache, non-cached failures with structured warning logs).
- `349ab69` step 2 (19 Nov): enabled `technical.bollinger-squeeze-breakout@1.0.0` in `configs/strategy-catalog.json` for
  research backtests only (paper/live remain unauthorized and empty); updated catalog tests (`BollingerSqueezeRecipeTests`,
  `ConfigCatalogServiceTests`, `StrategyArtifactCatalogTests`, `TestRepository`).
- 19 Nov 13:02 UTC site installation: Release `349ab69` installed on `C:\project\tradingflow-site` (port 10088, PID 49956)
  with 0 Alpaca paper positions, 0 open orders, and 0 queued/running jobs; replaced build kept as
  `bin-before-349ab69-20261119`. `/health` `ok`, `/health/trading-readiness` `ready`, FinBERT sidecar `ready`, 0 errors in
  `site.log`. `technical.bollinger-squeeze-breakout@1.0.0` is now selectable for research backtests on the Backtests page.
- `d252620` checkpoint 1 (18 Nov): `regime.sma_rising_lookback_bars`, ADR% screen rule (`--min-average-range-pct`,
  `daily_eligibility.min_average_range_pct`), Bollinger width rank + setup `bollinger_squeeze_breakout`, recipe
  `configs/backtest/strategies/bollinger-squeeze-breakout.bt-v1.yaml`, ORB set aside, ETF strategies blocked.
- `ef538c9` checkpoint 2 (18 Nov): `regime.nasdaq_listing_benchmark: QQQ` (option A). Backtests read listings from the frozen
  security master (dataset `6720a7d927abee37f61685306bb2b7e66e270b85b0133164e83b5feeb22af3ce`, 30 Oct 2026, collected
  with `status=all`), named by `research-daily-screen --security-master <id>` →
  `providers.evidence_catalog.security_master_dataset`. Paper/Desk read the broker's asset record. Reasons:
  `market_regime_off_on_entry_day`, `nasdaq_market_regime_off_on_entry_day`, `listing_exchange_unknown`,
  `listing_exchange_lookup_failed`. Each run logs listing coverage (warning above 5 %).
- `8732336` installed checkpoints 1+2 on `C:\project\tradingflow-site` (port 10088), previous build kept as
  `bin-before-ef538c9-20261118` (now superseded by `349ab69`).
- `532d8ac` browser-test hosts read a test-owned catalog (`TRADINGFLOW_UI_STRATEGY_CATALOG`, UI test mode only);
  .NET tests use `TestRepository.SetAsideStrategiesEnabledCatalog`.
- `bd35d1b` registered experiment V2-01 (median true range).
- Earlier on 18 Nov: FinBERT sidecar live on 127.0.0.1:8088 (started by `update-site.ps1`), Desk "Add stock",
  Audit page fixes, Earnings fixes (see their docs under `docs/verification/` and `docs/design-review/2026-11-18/`).

Verification: Checkpoint 3 + Step 2 — solution build 0 warnings / 0 errors across 18 projects; 140/140 focused VIX tests;
272/272 catalog/workflow tests; full .NET suite 4,288/4,295 (6 skipped, 1 5s load-timing flake in
`LiveRunnerIntegrationTests` that passed 37/37 alone); two independent reviewers accepted every checkpoint and Step 2.
Earlier (checkpoint 2): 121/121 affected browser specs with the test catalog.

## Next steps, in order

1. **6-month diagnostic run:** publish an ADR% screen with `research-daily-screen` (min price 5, average dollar volume
   20 M, ADR% 2.5, min history 253, `--exclude-symbols` fund list, `--sessions`, `--security-master`, `--fred-vix VIXCLS`),
   May–Oct 2026; report the five-minute bar count/size to the user **before** collecting (`evidence-plan-minute-bars`); run
   the three versions through the normal saved-plan workflow; deliver the full `.log`; report monthly per version; show
   listing coverage and the share of gap-inflated trades (reviewer conditions).
2. **Build Swing ORB on news (Option B, approved):** decide after close, enter next open (`technical.news-orb-swing@1.0.0`).
3. **Design (each needs written approval):** 21-EMA pullback long (+ registered experiment V2-01 MTR-14), short mirrors
   (reversed regime, borrowing rule, SEC Rule 201), and web pages to build research screens / collect five-minute bars.

## Facts checked today (do not re-derive)

- Alpaca SIP daily history: SPY/QQQ/sector funds from 4 Jan 2016; XLC from 19 Jun 2018; QQQM/QQQJ from 13 Oct 2020.
  Alpaca has no VIX index (VIXY/VIXM are futures funds, not comparable to VIX levels).
- Gemini examples checked against real data: MU (Sep 2026) and INTC (Nov 2026) prices match; most indicator claims did
  not (wrong ATR, "rising 50-day" false, RVOL claims false, 21-EMA "kiss" false). Treat Gemini numbers as unverified.
- Engine rule (unchanged, documented): a daily strategy's stop is placed at the fill day's close and checked from the next
  session; the entry day itself is unprotected in backtests.

## Housekeeping

- Session scratch: `C:\Users\manis\AppData\Local\Temp\claude\a79812ed036102d8\2811dc33-6e5c-48aa-af0f-1464f62dbd24\scratchpad`
  (mu-check.js, stock-check.js, intc\, vix\VIXCLS.csv) — disposable, not project data.
- Operator site: `C:\project\tradingflow-site` (`update-site.ps1`; check Alpaca positions/open orders and queued jobs
  before any restart; never restart in the Finviz capture windows 14:00–14:10 and ~21:05–21:30 UTC).
