# Design: market regime gate and Bollinger squeeze breakout (swing) — 18 November 2026

Status: **approved by the user on 18 November 2026 with option A ("Approve with A").** Implemented in checkpoints:

- **Checkpoint 1 (done):** rising-average regime rule, ADR% screen rule, Bollinger width rank and the
  `bollinger_squeeze_breakout` setup, recipe and catalog registration, same-day opening-range versions set aside. The
  squeeze is registered **disabled ("not yet runnable")**: no economic run until checkpoints 2 and 3 exist.
- **Checkpoint 2:** the per-stock rule of option A (Nasdaq-listed stocks also need QQQ ON), from the listing exchange in
  the security master of 30 Oct 2026 (Alpaca asset records).
- **Checkpoint 3 (user decision, 18 Nov):** VIX from FRED `VIXCLS`, always the previous trading day's close: full risk
  below 18, half risk from 18 to 22, no new long entries above 22. Half risk means half of the strategy's normal risk
  (0.5 % → 0.25 %). The relative-strength rule suggested for the 18–22 zone is not included (no frozen numbers).
- **Not yet assigned:** the sector-rank trade labels of section 4 (report only); they are added when the first
  development run's report is built, before any result is read.

User decisions recorded 18 November 2026:

- **ETF strategies blocked.** `etf.trend-10m` and `etf.buy-and-hold-control` are disabled ("index and sector funds are
  used only to judge the market and sector trend"); ETF rotation stays parked; the same-day opening-range versions are set
  aside. Until the squeeze is enabled, no strategy is runnable.
- **First run is a 6-month diagnostic, holdout spent.** Once checkpoints 2 and 3 are reviewed and the squeeze is enabled,
  it runs on the last six months of the frozen whole-market data (about 1 May – 30 October 2026) in three versions (no
  gate; SPY + QQQ; SPY + QQQ + VIX sizing), with a full `.log` file of the run. The user chose this knowing those months are
  then **spent** as a test period: results are labelled diagnostic, the evidence catalog records the consumption, and
  they can never be presented as an untouched out-of-sample test. The 2017–2023 development run remains to be done.

Implementation notes and differences found while building (checkpoint 1):

- Relative volume is the engine's existing measure: the day's volume over the **median** of the previous 20 sessions
  (not the average). The recipe states `volume_confirmation_mode: hard_gate` explicitly (it is also the parser default).
- ADR% follows the user's definition (18 Nov): mean(high − low) over 20 sessions ÷ latest completed close × 100.
- New admission profile `bollinger-squeeze-as-traded-research-v1` with derived-indicator profile 2.4.0 (adds the band-width
  rank); existing strategies' content hashes are unchanged (new fields are omitted when unset).
- **Stop timing (existing engine rule, not changed):** a daily strategy's stop is placed at the close of the fill day and
  checked from the next session; a fall through the stop on the entry day is not counted until then. Five-minute bars
  check stop and target only from the session after the fill. Changing this affects every daily strategy and needs its
  own design.
- Tests that exercise the opening-range machinery read a test-owned catalog copy with those versions enabled
  (`TestRepository.OpeningRangeEnabledCatalog`); the operator catalog keeps them set aside.
- Saved backtest plans cannot set ADR% (their eligibility form has no range field); ADR% is applied through the
  research-screen command and run-profile YAML, which is how this design's universe is built.

Requested by the user on 18 November 2026: "Regime gate + squeeze" from the Gemini ("Argon") swing plan, with the
additions "SPY / QQQ / QQQJ / QQQM", "a stock can be S&P 500 but also in any of these" and "find out top sector ETF".
The user also said to leave same-day (intraday) strategies for now. This is research: a positive result does not
authorize paper or live trading (operating boundaries, promotion gates).

## 1. What already exists and is reused

| Need | Existing part | Gap |
|---|---|---|
| Market regime check shared by backtest and paper | `RegimeGateService`, `RegimeCalendarBuilder`, recipe block `regime:` (one benchmark, "close above N-day average") | no "average is rising" rule; one fund only |
| Every listed stock, judged each day on earlier days only | `research-daily-screen` command, `DailyScreen`, published research screens (e.g. `connors-whole-market-20170101-20261030`, 2.56 M stock-days) | no "average daily range %" rule |
| Strongest sector funds | published screen `sector-funds-top3-return-12-1-20170101-20261030` (11 SPDR sector funds with their 12-1 month return) and the `etf.rotation-12-1` ranking | stocks carry only **today's** Finviz sector |
| 10-day breakout, volume spike, close near the high | `recent_high_lookback_bars`, `min_volume_spike`, `min_close_location_value` | — |
| Bollinger bands (20, 2) | `IndicatorEngine` (Skender) | no band-width rank |
| ATR stop, R-multiple target, holding limit, next-open entry | `stop_atr_multiple`, `target_r_multiple`, `max_hold_bars`, completed-bar next-open execution | — |
| Stop and target checked on 5-minute bars from the fill (1 Nov 2026 rule) | execution replay `DailyRthFiveMinuteV1` (daily long strategies) | 5-minute bars must be collected for traded stock-sessions |

## 2. Index funds: what the data allows

Alpaca SIP daily history (checked 18 Nov 2026): SPY, QQQ and ten of the sector funds start 4 Jan 2016 (Alpaca's
earliest); XLC starts 19 Jun 2018; **QQQM and QQQJ start 13 Oct 2020** (their launch).

- **QQQM tracks the same Nasdaq-100 index as QQQ** (cheaper share class). Its above/below-average signal is the same as
  QQQ's but with 4¾ fewer years of history, so it adds nothing to a gate. Proposed: use QQQ for the Nasdaq-100.
- **QQQJ** (Nasdaq Next Gen 100: the next 100 Nasdaq companies after the Nasdaq-100) has a 50-day average only from
  about late December 2020. Before that it cannot be evaluated.
- **No historical index-membership data exists in the project.** Which stocks were in the S&P 500, Nasdaq-100 or Next
  Gen 100 on a past date is unknown; Finviz shows only today's S&P 500 / Nasdaq-100 labels. Using today's labels for
  2017–2023 would mark stocks as members before they joined (survivor bias), which makes results diagnostic only.

## 3. Regime gate (shared by every swing strategy)

Rule per fund, evaluated on the completed daily bar before the entry decision:
**ON** when the fund's close is above its 50-day simple average **and** that average is higher than it was 20
sessions earlier (rising over about a month, the convention used in Minervini's trend template). Otherwise OFF. Missing
fund data → OFF (fail closed, as today).

Per-stock rule — **decision needed (A recommended):**

- **A. Broad market plus home market (recommended).** SPY must be ON for every entry. A stock listed on Nasdaq also
  needs QQQ ON. Listing exchange comes from Alpaca's asset record (current, but a listing exchange rarely changes; a
  known small bias). Uses only fund prices, so the full 2017–2026 history is usable.
- **B. Same as A plus QQQJ** for Nasdaq stocks below the Nasdaq-100 size range — not possible to decide historically
  without membership data and only from Dec 2020; would be a later, separately labelled variant.
- **C. Today's index labels** (Finviz S&P 500 / Nasdaq-100) per stock: closest to "the stock's own index", but every
  historical result becomes diagnostic only (see section 2).

Not included in this version (each would be a later, separately counted variant): halving position size instead of
blocking entries in a weak market; the intraday "SPY falls more than 1.2 × ATR" rule (intraday is set aside).

Recipe change: the existing `regime:` block gains `sma_rising_lookback_bars: 20` and `nasdaq_listing_benchmark: QQQ`
(option A). Live/paper and backtest keep using the same `RegimeGateService` code.

## 4. Top sector funds

Rank the 11 SPDR sector funds (XLK, XLF, XLE, XLV, XLY, XLP, XLI, XLB, XLU, XLRE, XLC) each month by their 12-month
return excluding the latest month (the existing published 12-1 screen; Moskowitz–Grinblatt industry momentum). The top
3 are "strong sectors".

Because a stock's sector is known only as of today (Finviz), the sector is **not a trading filter in version 1**.
Instead every simulated trade is labelled with today's Finviz sector and that day's rank of its sector fund, and the
results are reported separately for "sector in top 3" and "other". If the split shows a clear difference, a filtered
version is a second, separately counted variant (still diagnostic for history; testable cleanly in paper going forward,
where today's sector is the correct sector). The Desk can also show the current top-3 sector funds; that is a display
addition and would be proposed separately.

## 5. Strategy: Bollinger squeeze breakout v1 (`technical.bollinger-squeeze-breakout@1.0.0`)

Source: John Bollinger, *Bollinger on Bollinger Bands* (2001), "The Squeeze" — band width at a low followed by a
breakout. Differences from the source (recorded in the recipe): band-width rank over 60 sessions instead of a
six-month low; breakout of the 10-session high instead of a band break; volume and close-location confirmations and
the ATR stop/target/time exit come from the user's plan, not from Bollinger.

**Universe (backtests):** a new published research screen over every listed US stock (funds excluded with the existing
fund list), each day judged only on the previous 20 completed sessions, as-traded prices:
price ≥ $5 (the existing screens' floor; an addition to the plan), average dollar volume ≥ $20 M, **average daily range
≥ 2.5 %** (ADR% as the user defined it on 18 Nov: average of (high − low) over 20 sessions ÷ latest completed close
× 100; new screen rule), at least 253 sessions of history (the
existing swing rule of 252 completed sessions plus the signal day). Stocks that later delisted stay in. Paper uses the
normal wishlist/Finviz discovery plus the same admission rules.

**Signal on a completed daily bar D (all must hold):**
1. *Squeeze:* the Bollinger (20, 2) band width ((upper − lower) ÷ middle) on day D−1 is in the lowest 15 % of the
   widths of the 60 sessions D−60 … D−1 (new indicator: band-width percentile, measured before the breakout day
   because the breakout itself widens the bands).
2. *Breakout:* close of D above the highest high of D−10 … D−1 (existing rule, 10 sessions).
3. *Volume:* volume of D at least 1.5 × the **median** daily volume of the earlier 20 sessions (the engine's existing
   relative-volume measure, enforced with `volume_confirmation_mode: hard_gate`; found during implementation: the
   engine uses the median, not the average, so one unusually heavy day does not raise "normal volume").
4. *Strong close:* close in the top 25 % of D's range (existing close-location rule, ≥ 0.75).
5. *Regime:* gate ON for D (section 3).

**Orders:** buy at the next session's open. Stop = fill − 1.0 × ATR(14) of D; target = fill + 2.5 × ATR(14)
(2.5 R); sell at the close of the 10th session if neither is hit. Stop and target are checked on 5-minute bars from the
session after the fill (the engine places a daily stop at the fill day's close; see "Stop timing" above). Risk 0.5 % of equity per trade (the plan's 0.5–1 % range, frozen at the lower end), position at most 20 % of
equity, at most 10 positions at once, one position per stock, existing commission/fee model and 5 bps slippage.

**Same-day strategies:** opening-range breakout 1.2.0, 2.0.0 and 2.1.0 are set to `enabled: false` in the catalog with
the reason "set aside 18 Nov 2026: same-day; work moved to swing strategies". Files, results and history are kept.

## 6. Test plan and evidence

- Development window: signals 3 Jan 2017 – 29 Dec 2023. **Untouched holdout: 2 Jan 2024 – 30 Oct 2026**, not run until
  the development result is reviewed and the rules are frozen (matches the existing signal-study split).
- Two runs, both registered in the trial registry: v1 with the gate, and v1 with the gate switched off (control, to
  show what the gate adds). No parameter search.
- Report monthly results (New York dates, month-opening equity) per strategy version, plus trade count, win rate,
  average win/loss in R, expectancy, maximum drawdown, per-year results, results with gate ON vs OFF days, and the
  sector-rank split from section 4. Compared with SPY buy-and-hold over the same months (the existing
  `etf.buy-and-hold-control`).
- Decision recorded as REJECT, RETAIN_RESEARCH or ADVANCE_TO_HOLDOUT.

## 7. Changes (smallest reusable set)

| Area | Change | Files |
|---|---|---|
| Regime | rising-average rule; home-market fund for Nasdaq listings | `StrategyDefinition.cs` (`RegimeRules`), `RegimeCalendar.cs`, `RegimeGateService.cs`, `SimpleYamlReader.cs` |
| Screen | `MinAverageRangePercent` rule and `--min-average-range-pct` flag | `DailyScreenRules.cs`, `DailyScreen.cs`, `DailyScreenCommand.cs` |
| Signal | band-width percentile (`bollinger_squeeze_lookback_bars`, `max_bollinger_width_percentile`) | `SignalGenerator*.cs`, `BasicStrategyEvaluator.cs`, `SimpleYamlReader.cs` |
| Trade labels | today's Finviz sector and sector-fund rank on each simulated trade (report only) | results/report writer (exact file chosen in implementation; no trading effect) |
| Recipe | `configs/backtest/strategies/bollinger-squeeze-breakout.bt-v1.yaml`, catalog entry, ORB entries disabled | `configs/` |
| Tests | rising-average gate (incl. fail-closed), Nasdaq home market, range-% screen, band-width percentile on fixed fixtures, recipe parse, shared backtest/paper gate parity | `TradingFlow.Tests` |

No database schema change. No new provider. Paper/live behaviour changes only for a strategy whose recipe declares the
new regime fields (none is enabled for paper).

## 8. Data and work

- Daily bars: the existing published whole-market daily dataset (2017 – 30 Oct 2026) and fund bars from Alpaca.
- Screen: one `research-daily-screen` run (minutes; about 2.5 M stock-days).
- 5-minute bars: collected only for stock-sessions with a simulated position (count known after the daily signal pass;
  reported before collecting, with the expected download size).
- Two independent reviews (plan/execution and code/design) and focused tests, then the two development runs.

## Verification — checkpoint 1 (18 November 2026)

- Full .NET suite on the final checkpoint-1 code: 4,252 passed, 2 failed, 6 skipped; the 2 failures (decision-context
  tests needing a selectable catalog strategy) were moved to the test-owned catalog copy and pass (26/26). Focused
  checkpoint suites 294/294.
- Reviewer A (plan/execution) and reviewer B (code/design) first requested changes (benchmark history planning, evidence
  keys, manifest bytes, warm-up, rejection detail, ADR% test, design record, squeeze disabled until checkpoints 2–3) and
  accepted after the fixes. The ETF strategies were then blocked at the user's request (catalog and test change only).
- Browser tests (Backtests, workflow and workspace specs): 113 passed, **8 failed** — each needs at least one runnable
  strategy on the Backtests page and the operator catalog now has none. The test site reads the repository catalog at a
  fixed path; letting it read a test-owned copy is a small product setting awaiting the user's decision.

## Checkpoint 2 — Nasdaq home-market rule (option A), 18 November 2026

- **Recipe:** `regime.nasdaq_listing_benchmark: QQQ`. Every entry needs SPY on (close above its rising 50-day average);
  a stock listed on Nasdaq also needs QQQ on under the same rule; other listings need only SPY. QQQM adds nothing (same
  index, shorter history); QQQJ is not used.
- **Refusal reasons** (identical in backtests, paper and the Desk): `market_regime_off_on_entry_day` (SPY off),
  `nasdaq_market_regime_off_on_entry_day` (QQQ off for a Nasdaq listing), `listing_exchange_unknown` (no listing known),
  and on the live/paper path `listing_exchange_lookup_failed` (the broker's asset lookup failed, for example a rate limit).
  Every missing piece refuses the entry; nothing is guessed.
- **Where the listing comes from:** backtests read the frozen Alpaca security-master snapshot of 30 Oct 2026 (dataset
  `6720a7d9…`, 33,556 assets, collected with `status=all`, so delisted stocks are included), named by
  `providers.evidence_catalog.security_master_dataset` and recorded in the research screen with
  `research-daily-screen --security-master <dataset ID>`. Paper trading and the Desk read the broker's current asset
  record (the Desk keeps listings for an hour across refreshes; one failed lookup affects only that stock).
- **Known bias, accepted with option A:** the snapshot is the current listing, applied to past dates. A stock that moved
  between exchanges is judged by today's exchange; a symbol the snapshot lists on two exchanges (reused ticker) is left
  out and refused. Each run logs how many of its stocks have no usable listing; above 5 % it is a warning to escalate to the
  operator (user rule: escalate only above 5 % universe loss).
- **Prepared runs** now also prepare QQQ history with the same early start as SPY.
- **Live/Desk difference:** when QQQ bars are missing, paper trading refuses with `nasdaq_market_regime_off_on_entry_day`
  (fail closed) while the Desk reports the stock's strategy check as "data missing"; both block the entry.
- **Not covered by unit tests:** a full evidence-catalog backtest through `BacktestRunner` with the home-market rule (it
  needs a published security-master fixture); the first real run's log records the listing coverage instead.
- **Verification:** focused suites 316/316; full .NET suite 4,259 passed, 1 failed, 6 skipped — the failure
  (`LiveRunnerIntegrationTests.RunAsync_SubmitsTechnicalExitClose_ForActiveStrategyPosition`, a strategy without a regime
  rule, so the changed path is not exercised) took 5 s under full-suite load and passed 3/3 alone and in its class (37/37):
  a timing flake, not a regression. Reviewer A and reviewer B requested changes (listing source behind prepared-data
  wrappers, QQQ not prepared, coverage count, lookup failures, Desk refresh robustness, ticker case, documentation) and
  accepted after the fixes. Condition from reviewer A: the first squeeze run's `.log` must show the listing coverage.

## Installation on the local site (checkpoints 1 and 2), 18 November 2026

Installed at 21:43 UTC after the Finviz close-capture window, with no Alpaca paper positions or open orders and no queued
or running job. Release `ef538c9`; the replaced build is kept as `bin-before-ef538c9-20261118`. Readiness `ready`, FinBERT
sidecar `ready`, no errors after start (two existing database-query notices). Effect for the operator: no strategy is
runnable (opening-range set aside, ETF strategies blocked, squeeze not yet runnable until checkpoint 3).

## Checkpoint 3 — VIX sizing from FRED, 18–19 November 2026

- **Recipe** (`regime:` block, all four together or none; omitted from serialized rules when unset, so other strategies keep
  their content hashes): `vix_series: VIXCLS`, `vix_full_risk_below: 18`, `vix_no_new_longs_above: 22`,
  `vix_reduced_risk_scale: 0.5`. `RegimeRules.Validate` (invoked by `SimpleYamlReader`, `RegimeCalendar.WithVix`, and
  `VixRegime.Decide`) refuses a partial block, another series, thresholds out of order, a scale outside (0, 1) and any
  strategy that is not `direction: long` (the rule was approved for long entries only).
- **Which VIX close & authoritative session alignment:** the close of the exchange session before the entry, defined as the
  date of the latest completed benchmark (SPY) daily bar known at the decision — the same close the SPY rule reads. When an
  authoritative exchange session schedule (`IMarketSessionScheduleProvider` / `candidateSessionSchedules`) is available,
  non-trading-day bars (such as Sunday overnight or holiday bars from sub-daily resampling) are ignored and a stale
  benchmark missing the expected previous completed trading session fails closed as `market_regime_off_on_entry_day` instead
  of falling back to an older VIX close (`D-2`). `RegimeCalendar` pre-indexes benchmark bars and trading-day schedules once
  for $O(\log N)$ zero-allocation lookups across multi-year whole-market runs. A value FRED lists for a holiday (for example
  7 Sep 2026) is never used; a session FRED has not published refuses the entry instead of falling back to an older close.
  Below 18: the strategy's normal risk (0.5 %). 18 to 22, both included: half (0.25 %). Above 22: no new long entry.
- **Refusal reasons** (identical in backtests, paper, mobile automation, shadow replay and the Desk via the shared
  `RegimeGateService.Evaluate` helper and `RegimeCalendar.EntryDecisionFor`): `vix_regime_off_on_entry_day` (above 22),
  `vix_close_unknown_for_previous_session` (FRED lists no close for that session), `vix_lookup_failed` (paper/Desk: the FRED
  request failed or no reader is configured). The SPY and QQQ checks come first.
- **One multiplier & shadow-replay parity:** `OrderPlanningRiskLimits.EntryRiskScale` (default 1; must be above 0 and at
  most 1) scales the account risk budget inside `SharedOrderRiskPlanner`. The daily portfolio, the event portfolio, the
  intraday replay, independent opportunity shadow replay (`BacktestOpportunityEntryEligibility` captures `RegimeRejection`
  and `EntryRiskScale`), the paper runner and mobile automation pass the scale their regime check returned. The event and
  intraday replays get refusals and scales from one helper (`BacktestRunner.EntryAdmissions`). The intraday replay did not
  read the regime calendar before; it now applies it at admission like the event portfolio (no intraday strategy declares a
  regime rule, so no result changes).
- **Backtest data, pre-flight protection & run evidence:** `research-daily-screen --fred-vix VIXCLS` downloads FRED's
  complete file once (with UTF-8 BOM stripping and `observation_date`/`DATE` header validation) and stores its exact bytes
  as `fred-vixcls.csv` with `fred-vixcls-receipt.json` (URL, request time, HTTP status, size, SHA-256, first and last
  observation date) in the screen folder. Pre-existing VIX CSV or receipt files in the output folder are refused up front
  whether or not `--fred-vix` is supplied, so earlier provider receipts are never overwritten. The screen manifest
  fingerprints both files; `ResearchScreenReference.FredVixSha256` carries the CSV hash into `SameInputs` and the trial
  registry (`fred-vixcls:<sha256>` in `ResearchQueue.WorkflowVariant` and `ResearchFileStart.Variant`). Pre-flight checks in
  `RunWorkflowService`, `BacktestPlanService`, `WorkflowRunConfiguration`, and `PreparedMarketDataService` refuse a VIX run
  without a frozen VIX screen (or a Nasdaq evidence-catalog run without `security_master_dataset`) before registering a
  trial start or downloading bars. `PublishedResearchScreen.ReadFrozenVix` re-hashes both `fred-vixcls.csv` and
  `fred-vixcls-receipt.json`, drops closes on or after a protected research boundary using cross-platform New York time,
  records the frozen VIX provenance (`fileName`, `sha256`, `receiptSha256`, `publishedCloses`, `firstDate`, `lastDate`,
  `protectedCutoffDate`) in `{runName}-daily-eligibility.json`, logs the hashes and date range, and warns when the copy ends
  before the run's last needed session (`windowEnd.AddTicks(-1)`).
- **Paper and Desk:** `FredVixCloseSource` (disposable, disposing each `HttpClient`, with gzip/deflate decompression and
  structured failure warning logs) reads `fredgraph.csv?id=VIXCLS` (last 40 days) and reuses a successful read for 15
  minutes; failures are not cached. Decision evidence in backtests (`EligibilityEvidence`), paper (`LiveRunner`), mobile
  automation and the Desk (`StockStrategyEvidenceLoader`) records the VIX decision and `RiskScale` only for strategies with
  VIX sizing, so other strategies' evidence bytes are unchanged.
- **Not changed:** the Desk's manual order ticket plan (operator-entered orders, not strategy entries) and operator-direct
  entries keep the profile's full risk budget. `technical.bollinger-squeeze-breakout@1.0.0` stays `enabled: false` in
  `configs/strategy-catalog.json` until Step 2 explicitly enables it after Checkpoint 3 sign-off.
- **Verification:** Solution build (`TradingFlow.sln`) succeeded with 0 warnings and 0 errors across all 18 projects.
  Focused suites passed 140/140. Full .NET suite passed 4,288/4,295 (1 failed, 6 skipped) — the single failure
  (`LiveRunnerIntegrationTests.RunAsync_BatchedCandlePipeline_WritesMetricsAndAuditsEndToEnd`, a strategy without a regime
  rule and a 5 s timeout) timed out under full-suite CPU load and passed 37/37 when `LiveRunnerIntegrationTests` ran in
  isolation. Independent Reviewer 1 (`plan-reviewer`, plan/causality/execution) and Reviewer 2 (`code-reviewer`,
  architecture/maintainability/config-driven design) requested changes in Round 1 (authoritative exchange-session freshness
  and non-trading-day bar filtering, opportunity shadow-replay scale and refusal parity, $O(\log N)$ pre-indexed
  benchmark/schedule lookups, pre-flight screen checks and trial-registry/run-evidence VIX hashes, receipt hash
  verification, resource disposal, BOM/header checks, DRY gate evaluation and structured failure warnings) and both
  accepted after the fixes.
