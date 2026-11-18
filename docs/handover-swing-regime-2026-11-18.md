# Handover — swing strategies, market regime and VIX (state at 18 November 2026, ~22:15 UTC)

For the next session. Everything below is committed on local `main` (not pushed; no GitHub sync without the user asking).
The user's own uncommitted edit `docs/research/tradingflow-audit-2026-11-14.md` must stay untouched and unstaged.

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
| `docs/design-review/2026-11-18/regime-gate-and-bollinger-squeeze.md` | approved (option A); checkpoints 1–2 done, 3 next |
| `docs/design-review/2026-11-18/swing-orb-and-vix-gate-proposal.md` | swing ORB approved with option B ("all approved"); VIX part superseded by the user's choice above |
| `docs/design-review/2026-11-18/experiment-v2-01-median-true-range.md` | registered experiment, host = 21-EMA pullback (not the squeeze) |
| `docs/operating-boundaries.md`, `AGENTS.md` | binding rules (design approval, two reviewers, install after review, data deletion rules) |

## Done today (commits)

- `d252620` checkpoint 1: `regime.sma_rising_lookback_bars`, ADR% screen rule (`--min-average-range-pct`,
  `daily_eligibility.min_average_range_pct`), Bollinger width rank + setup `bollinger_squeeze_breakout`, recipe
  `configs/backtest/strategies/bollinger-squeeze-breakout.bt-v1.yaml`, ORB set aside, ETF strategies blocked.
- `ef538c9` checkpoint 2: `regime.nasdaq_listing_benchmark: QQQ` (option A). Backtests read listings from the frozen
  security master (dataset `6720a7d927abee37f61685306bb2b7e66e270b85b0133164e83b5feeb22af3ce`, 30 Oct 2026, collected
  with `status=all`), named by `research-daily-screen --security-master <id>` →
  `providers.evidence_catalog.security_master_dataset`. Paper/Desk read the broker's asset record. Reasons:
  `market_regime_off_on_entry_day`, `nasdaq_market_regime_off_on_entry_day`, `listing_exchange_unknown`,
  `listing_exchange_lookup_failed`. Each run logs listing coverage (warning above 5 %).
- `8732336` installed checkpoints 1+2 on `C:\project\tradingflow-site` (port 10088), previous build kept as
  `bin-before-ef538c9-20261118`. **No strategy is runnable on the site right now** (by design until checkpoint 3).
- `532d8ac` browser-test hosts read a test-owned catalog (`TRADINGFLOW_UI_STRATEGY_CATALOG`, UI test mode only);
  .NET tests use `TestRepository.SetAsideStrategiesEnabledCatalog`.
- `bd35d1b` registered experiment V2-01 (median true range).
- Earlier the same day: FinBERT sidecar live on 127.0.0.1:8088 (started by `update-site.ps1`), Desk "Add stock",
  Audit page fixes, Earnings fixes (see their docs under `docs/verification/` and `docs/design-review/2026-11-18/`).

Verification: full .NET suite 4,259/4,266 on checkpoint 2 (6 skipped, 1 load-timing flake that passes alone);
121/121 affected browser specs with the test catalog; two independent reviewers accepted every checkpoint.

## Next steps, in order

1. **Checkpoint 3 — VIX sizing** (approved): new recipe block (e.g. `regime.vix_series: VIXCLS`, `vix_full_below: 18`,
   `vix_off_above: 22`, half risk in between). Data: FRED `https://fred.stlouisfed.org/graph/fredgraph.csv?id=VIXCLS`.
   Backtests need a **frozen, fingerprinted copy** (recorded in run evidence like other datasets); paper reads FRED's
   latest published close. FRED lists a value on US market holidays (7 Sep 2026) — match to exchange sessions.
   Risk scaling goes into the shared order planner: per-trade risk budget is read from `run.Portfolio.AccountRiskBudgetPct`
   in `BacktestRunner.Diagnostics.cs:~705`, `EventPortfolioExecution.cs:237`, `IntradayPortfolioExecution.cs:287`,
   `LiveRunner.ProcessTicker.cs:~490` — apply one per-entry multiplier (1.0 / 0.5 / refuse with reason
   `vix_regime_off_on_entry_day`) so all paths stay identical. Tests, two reviewers, full suite, install.
2. **Enable the squeeze** in `configs/strategy-catalog.json` (remove `enabled:false` "Not yet runnable"), update the
   catalog tests (`ConfigCatalogServiceTests`, `StrategyArtifactCatalogTests`, `BollingerSqueezeRecipeTests`).
3. **6-month diagnostic run:** publish an ADR% screen with `research-daily-screen` (min price 5, average dollar volume
   20 M, ADR% 2.5, min history 253, `--exclude-symbols` fund list, `--sessions`, `--security-master`), May–Oct 2026; report
   the five-minute bar count/size to the user **before** collecting (`evidence-plan-minute-bars`); run the three versions
   through the normal saved-plan workflow; deliver the full `.log`; report monthly per version; show listing coverage
   and the share of gap-inflated trades (reviewer conditions).
4. Design (each needs written approval): swing ORB build (option B), 21-EMA pullback long (+ V2-01), short mirrors
   (reversed regime, borrowing rule, SEC Rule 201).

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
