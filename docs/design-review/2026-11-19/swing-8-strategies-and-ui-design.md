# Design: All 8 Swing Strategies (4 Long + 4 Short), Web-App Runner & Paper Filter — 19 November 2026

Status: **approved by the user on 19 November 2026.**

Checkpoint 3 (`ede0ce2`) and enabling `technical.bollinger-squeeze-breakout@1.0.0` (`349ab69`) were completed, reviewed, tested, and installed on `http://127.0.0.1:10088` earlier on 19 November 2026 (`3658ee4`).

## 1. The 4 + 4 Strategy Matrix

| # | Archetype | Long Strategy (`direction: long`) | Short Mirror (`direction: short`) | Checkpoint |
|---|---|---|---|---|
| **1** | **Volatility Squeeze** | `technical.bollinger-squeeze-breakout@1.0.0` — 60d BB width rank <= 15%, 10d high breakout, RVOL >= 1.5, CLV >= 0.75 | `technical.bollinger-squeeze-breakdown@1.0.0` — 60d BB width rank <= 15%, 10d low breakdown, RVOL >= 1.5, CLV <= 0.25 | Long = done (`349ab69`); Short = Checkpoint 8 |
| **2** | **Post-Catalyst Drift / Swing ORB** | `technical.news-orb-swing@1.0.0` (Option B) — Tier-1 positive news (<= 18h, +5m receipt delay), gap up >= 0.75 * ATR(14), first-30m RVOL >= 2.0, close > 10:00 NY high & session VWAP | `technical.news-orb-breakdown@1.0.0` (Option B) — Tier-1 negative news (<= 18h, +5m receipt delay), gap down >= 0.75 * ATR(14), first-30m RVOL >= 2.0, stays <= session VWAP in first 30m, close < 10:00 NY low & session VWAP | Long = Checkpoint 4; Short = Checkpoint 8 |
| **3** | **21-EMA Trend Pullback / Rejection** | `technical.ema21-trend-pullback@1.0.0` (+ V2-01 `1.1.0` MTR-14) — Close > rising SMA(50), 1–5 bar low-volume pullback (RVOL < 0.85) into within 0.5 * ATR(14) of EMA(21) while low stays > SMA(50), trigger closes > prior high | `technical.ema21-rejection-short@1.0.0` (+ V2-01 `1.1.0` MTR-14) — Close < falling SMA(50), 1–5 bar weak-volume bounce (RVOL < 0.85) into within 0.5 * ATR(14) of EMA(21) while high stays < SMA(50), trigger closes < prior low | Long = Checkpoint 5; Short = Checkpoint 8 |
| **4** | **Rubber-Band Snap (Mean Reversion / Parabolic Exhaustion)** | `technical.rubber-band-reclaim@1.0.0` — Stock > rising SMA(50); stretch bar within prior 3 sessions closes < lower Bollinger Band and RSI(2) < 10 with no fresh news within 48h; trigger bar closes > prior bar's high | `technical.parabolic-exhaustion-short@1.0.0` — Prior bar spikes >= 2.5 * ATR(14) above EMA(21) on a shooting-star candle (upper wick >= 2 * body, CLV <= 0.35); trigger bar closes < prior bar's low | Long = Checkpoint 6; Short = Checkpoint 8 |

**Why Archetype #4 differs from the retired 13 Nov Connors RSI-2:** The 13 Nov study retired unprotected Connors RSI-2 (`no stop`, `SMA-5` exit, no ADR% filter, no VIX/QQQ gate). Here, **#4 Short** (parabolic >= 2.5 ATR above EMA-21 + shooting star + prior-low break) was never tested before, and **#4 Long** states a distinct hypothesis: high-ADR (>= 2.5%) stocks in rising 50-SMA uptrends under the SPY/QQQ/VIX gate, entering only on a confirmed prior-high reclaim with a 48h no-news veto, 1.0 ATR stop, 2.5R target, and 10-session hold.

## 2. Shared Portfolio, Serialization & Admission Rules

- **Universe rules:** As-traded price >= $5, 20-session average dollar volume >= $20M, ADR% >= 2.5%, >= 253 daily sessions, ETFs/funds excluded, split-in-lookback excluded for as-traded screens.
- **Timing & Exits:** Signal on completed daily bar D; fill at session D+1 open; initial stop = 1.0 * V_D (V_D = ATR(14)_D, or MTR(14)_D for Experiment V2-01 on Archetype #3); target = 2.5 * V_D (2.5R); time exit at close of session 10; 5-minute RTH stop/target replay from session after fill; 0.5% normal account risk per trade, 20% max position notional, max 10 concurrent positions, 5 bps slippage + standard fee model.
- **Serialization safety:** Every new `EntryRules`, `ExitRules`, `RegimeRules`, and `PortfolioConfig` property uses `[JsonIgnore(WhenWritingNull / WhenWritingDefault)]` so existing strategy and catalog hashes remain byte-for-byte unchanged.
- **Paper / Desk Strategy-Owned Stock Filter:** Optional `min_average_range_pct` (`2.5`) and `min_average_dollar_volume` (`20000000`) on `EntryRules` (evaluated on the prior 20 completed daily bars in `SignalGenerator` / `BasicStrategyEvaluator`) so Paper trading and the Desk enforce the exact same ADR% and liquidity floor whether stocks arrive from a Finviz preset, a manual Wishlist, or both.

## 3. Checkpoints 4–10 Specification

### Checkpoint 4 — Long #2: Swing ORB on News (`technical.news-orb-swing@1.0.0`, Option B)
- Daily sub-session confirmation evidence derived from completed 5-minute RTH bars of day D (no look-ahead): `opening_range_minutes: 30` (`09:30–10:00` NY high/low), `min_opening_relative_volume: 2.0` (first-30m volume vs prior 20-session median first-30m volume), `require_daily_close_above_opening_range_high: true`, `require_daily_close_above_session_vwap: true`.
- Gap threshold in ATR units: `min_gap_up_atr: 0.75` (`(Open_D - Close_{D-1}) / ATR(14)_{D-1} >= 0.75`).
- Frozen conservative historical news contract: `catalyst_receipt_delay_minutes: 5` on `max(created_at, updated_at)` within `max_news_age_hours: 18`, Tier-1 categories (`earnings`, `guidance`, `fda`, `contract`, `upgrade`), and FinBERT negative veto `veto_news_sentiment_below: -0.15`.

### Checkpoint 5 — Long #3: 21-EMA Trend Pullback (`technical.ema21-trend-pullback@1.0.0`) + V2-01 (`1.1.0` MTR-14)
- Add `Ema21` and `Mtr14` (median of 14 true ranges, for Experiment V2-01) to `IndicatorSnapshot` and `IndicatorEngine` (`JsonIgnore` when null; new swing admission profile `derivedIndicatorProfileVersion: 2.5.0`).
- Generic config fields: `sma50_rising_lookback_bars: 20`, `setup_type: ema21_pullback_reclaim` (`pullback_lookback_bars: 5`, `max_ema21_pullback_atr: 0.5`, `max_pullback_relative_volume: 0.85`, `require_pullback_low_above_sma50: true`, trigger bar `close > prior_bar_high`), and `volatility_unit: atr14 | mtr14` on `ExitRules` for V2-01 (`technical.ema21-trend-pullback@1.1.0`).

### Checkpoint 6 — Long #4: Rubber-Band Reclaim (`technical.rubber-band-reclaim@1.0.0`)
- Reuses `setup_type: mean_reversion_reclaim`, `EnableLowerBollingerStretch`, `MaxReversionRsi2: 10.0`, `ReversionStretchLookbackBars: 3`, `VetoFreshNewsHours: 48.0`, `RequirePriceAboveSma50: true`, `Sma50RisingLookbackBars: 20`.
- Adds `require_combined_bollinger_and_rsi2_stretch: true` so the stretch bar satisfies **both** `Close < BollingerLower` **and** `Rsi2 < 10` before reclaiming the prior bar's high, with `SPY+QQQ+VIX` gate and `1.0 ATR` / `2.5R` / `10d` exits.

### Checkpoint 7 — Shared Short Foundation (Reversed Regime + Borrow + SEC Rule 201)
- `rule: price_below_falling_sma` — ON when `SPY` close < `SMA(50)` **and** `SMA(50)` is lower than `20` sessions earlier; Nasdaq-listed stocks also require `QQQ` under the same rule (`nasdaq_listing_benchmark: QQQ`).
- VIX short sizing (`FRED VIXCLS` previous session close): `vix_no_new_shorts_below: 18` (refused as `vix_short_regime_off_on_entry_day`), `18 <= VIX <= 22` half risk (`0.5x` -> `0.25%`), `VIX > 22` full risk (`1.0x` -> `0.5%`). Missing VIX fails closed.
- Frozen short-borrow & cost contract: backtests charge config-driven annualized borrow fee (`short_borrow_apr_pct: 1.0`, accrued per calendar day held) in portfolio accounting; paper/Desk additionally require Alpaca's live `Shortable && EasyToBorrow` flags (`short_not_easy_to_borrow`).
- SEC Rule 201: if stock low on session D-1 or D reaches `<= 0.90 * PriorClose`, Rule 201 is active through the end of the next exchange session; market/next-open short entries on an active Rule 201 session are refused (`sec_rule_201_short_restriction`).

### Checkpoint 8 — All 4 Short Mirror Strategies (`1.0.0` each, plus V2-01 Short `1.1.0`)
1. `technical.bollinger-squeeze-breakdown@1.0.0`: `short_setup_type: bollinger_squeeze_breakdown` (60d width rank <= 0.15, close < 10-session low, RVOL >= 1.5, CLV <= 0.25).
2. `technical.news-orb-breakdown@1.0.0`: `short_setup_type: news_orb_breakdown` (Tier-1 negative news <= 18h with +5m receipt delay & positive-sentiment veto >= +0.15, gap down >= 0.75 * ATR(14), first-30m RVOL >= 2.0, first-30m bars never reclaim session VWAP, daily close < 10:00 NY low & session VWAP).
3. `technical.ema21-rejection-short@1.0.0` (+ V2-01 `1.1.0` MTR-14): `short_setup_type: ema21_rejection_breakdown` (close < falling SMA-50, 1–5 bar weak bounce RVOL < 0.85 within 0.5 * ATR(14) of EMA-21 while high stays < SMA-50, trigger closes < prior low).
4. `technical.parabolic-exhaustion-short@1.0.0`: `short_setup_type: parabolic_shooting_star_breakdown` (D-1 high >= EMA(21) + 2.5 * ATR(14), shooting star with upper wick >= 2 * body and CLV <= 0.35, day D closes < D-1 low).

### Checkpoint 9 — Web UI Research Screen Builder & Two-Step 5-Minute Bar Collector
- Add a **Research Screens & Minute Bars** panel to the Backtests workspace backed by durable jobs:
  - *Build Screen:* runs `DailyScreen` + `--security-master` + `--fred-vix VIXCLS` and registers the published screen for immediate selection on Backtests.
  - *Plan & Collect 5-Minute Bars:* Step 1 runs the dry-run planner (`evidence-plan-minute-bars`) and displays exact symbol-session count and estimated download size; Step 2 downloads only after clicking **Confirm & Collect**.

### Checkpoint 10 — 6-Month Diagnostic Run (1 May – 30 Oct 2026) & Report
- Build the frozen May–Oct 2026 screen, inspect & confirm the 5-minute bar download estimate, run all enabled long and short strategies through the saved-plan workflow, deliver the complete `.log` file, and publish monthly NY-session performance tables per strategy version (with sector-fund top-3 vs other labels, listing coverage, and gap-inflated trade shares).

## 4. Checkpoint Verification Log

- **Batch 1 — Checkpoints 5 & 6 + Strategy-Owned ADR%/Liquidity Filter (19 Nov 2026):**
  - Implemented `Ema21`, `Mtr14`, `MaxTr14` (`derivedIndicatorProfileVersion: "2.5.0"`, `swing-daily-v2-as-traded-research-v1`), strategy-owned 20-session `min_average_range_pct` & `min_average_dollar_volume` matching `DailyScreen`, `sma50_rising_lookback_bars`, `setup_type: ema21_pullback_reclaim`, `exit_rules.volatility_unit` (`atr14` | `mtr14`), `require_combined_bollinger_and_rsi2_stretch`, and catalog recipes `technical.ema21-trend-pullback@1.0.0` (`0a2cac80c7a629eae0a9c1def135ae757a806210545e4d4ca9e5b9884452e72c`), `technical.ema21-trend-pullback@1.1.0` (`445cef6d533becfb1a76b87c1d4187a502a9f7bc39b0836fb90271aa72592eec`, `derivedFromIdentity` bound to `1.0.0`), and `technical.rubber-band-reclaim@1.0.0` (`d556e04c828b9c2096be3c2e88ac3da937d076d47dac659f75c4a95e33b953c`).
  - Verification: `TradingFlow.sln` build `0 warnings / 0 errors` across all 18 projects; `163/163` focused tests green (`SwingLongArchetypesTests`, `BollingerSqueezeRecipeTests`, `ConfigCatalogServiceTests`, `StrategyArtifactCatalogTests`, `KeptStrategyIdentityTests`, `VixSizingTests`, `DailyScreen`, `ResearchScreenTests`, `RunConfigParsingTests`, `RunConfigWriter`); existing canonical hashes (`technical.bollinger-squeeze-breakout@1.0.0` = `ee56bf94...`, kept/retired artifacts) verified unchanged.
  - Independent reviews: Code & Design reviewer **ACCEPT** (`#40`, 3 low-priority polish notes queued for Batch 2 / Checkpoint 8); Plan, Causality & Execution reviewer **ACCEPT** (`#45`).
