# Design: market regime gate and Bollinger squeeze breakout (swing) — 18 November 2026

Status: **proposed, awaiting user approval.** Nothing below is implemented.

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

Recipe change: the existing `regime:` block gains `sma_rising_lookback_bars: 20` and `home_market: nasdaq_listing_qqq`
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
≥ 2.5 %** (mean of (high − low) ÷ close over 20 sessions; new screen rule), at least 253 sessions of history (the
existing swing rule of 252 completed sessions plus the signal day). Stocks that later delisted stay in. Paper uses the
normal wishlist/Finviz discovery plus the same admission rules.

**Signal on a completed daily bar D (all must hold):**
1. *Squeeze:* the Bollinger (20, 2) band width ((upper − lower) ÷ middle) on day D−1 is in the lowest 15 % of the
   widths of the 60 sessions D−60 … D−1 (new indicator: band-width percentile, measured before the breakout day
   because the breakout itself widens the bands).
2. *Breakout:* close of D above the highest high of D−10 … D−1 (existing rule, 10 sessions).
3. *Volume:* volume of D at least 1.5 × the average daily volume of the earlier 20 sessions (existing relative-volume
   rule; its baseline is pinned in a test).
4. *Strong close:* close in the top 25 % of D's range (existing close-location rule, ≥ 0.75).
5. *Regime:* gate ON for D (section 3).

**Orders:** buy at the next session's open. Stop = fill − 1.0 × ATR(14) of D; target = fill + 2.5 × ATR(14)
(2.5 R); sell at the close of the 10th session if neither is hit. Stop and target are checked on 5-minute bars from the
fill. Risk 0.5 % of equity per trade (the plan's 0.5–1 % range, frozen at the lower end), position at most 20 % of
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
