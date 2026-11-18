# Proposal: swing opening-range breakout on news, and a VIX market gate — 18 November 2026

Status: **proposed, awaiting user decisions.** Nothing below is implemented. Requested by the user on 18 November 2026
("also write Swing ORB"; "try VIX"; "VIX below 18 = green light for buying; above 20–22 = caution / shorting / cash").
Research only: a positive result does not authorize paper or live trading.

## Part 1 — Swing ORB (Strategy A of the Gemini plan)

**The plan's rule:** a big-news catalyst in the last 18 hours, an opening gap above 0.75 × ATR(14), first-30-minute
relative volume above 2, and price breaking above the 09:30–10:00 New York high while above intraday VWAP; then hold as
a swing trade (stop 1 ATR, target 2.5 ATR, at most 10 sessions). The user set aside same-day trading, so the position is
held overnight, not closed at 15:55 like the earlier opening-range versions.

### What exists

- Opening-range machinery (`OpeningRangeBreakoutEvaluator`, five-minute replay, range high/low, relative opening volume),
  built for same-day exits; the three same-day versions were set aside on 18 Nov.
- News: Alpaca news (Benzinga and other sources) and Finviz news are stored; FinBERT now scores them locally. Strategies
  already read a catalyst, its age and sentiment (`require_positive_news`, `max_news_age_hours`, `min_news_sentiment`).
  Backtests that use news need a frozen **news evidence package**.
- Gap, VWAP and ATR rules exist (`min_gap_up_pct`, `require_price_above_vwap`, ATR stop and R target, `max_hold_bars`).
- Daily stops: the engine places a daily strategy's stop at the close of the fill day and checks it from the next
  session (see "Known difference" below).

### The real obstacle: when did the news become known?

Old news downloaded today carries a publication or update time, but there is no record of when TradingFlow would have
first received it. The operating boundaries make news-based backtests diagnostic unless a cautious rule is frozen in
advance. Proposed rule: a story counts only from its provider **update** time (the later of created/updated), plus a fixed
delay of 5 minutes; a story updated after the decision time does not count. "Big news" is limited to categories the
existing rule classifier recognises (earnings results, guidance, FDA decisions, contracts/partnerships, analyst upgrades);
FinBERT's score may veto a clearly negative story but cannot qualify one by itself.

### Entry options — decision needed

- **A. True intraday entry, swing hold (closest to the plan).** Enter when a completed five-minute bar after 10:00 closes
  above the 09:30–10:00 high and above VWAP; hold up to 10 sessions. Needs a new shared execution path, "intraday entry,
  multi-session hold", in backtest **and** paper (today intraday positions are closed the same day). Largest work.
- **B. Decide at the close, enter next open (smallest change).** On the catalyst day, after the close, check: gap, first-30-
  minute volume, a close above the 10:00 high and above the day's VWAP; buy at the next open as a normal daily swing
  strategy. Reuses the daily path that the squeeze strategy uses. Enters one day later than the plan, so it is a
  different, labelled variant.
- **C. Decide at 15:00, enter 15:00–16:00 (middle).** Uses the existing decision-hour timing (approved 12 Nov) with the
  10:00 high and VWAP from completed bars. Backtest-only today: the paper runner does not decide at the decision hour.

Recommendation: **B first** (it can run on the same data and workflow as the squeeze), then A only if B shows an edge
worth the engine work.

### Data and work

- Five-minute bars for gap days with qualifying news (count reported before collecting), news evidence package for the
  test window, and the ADR% ≥ 2.5 / $20 M dollar-volume screen shared with the squeeze.
- New pieces for option B: first-30-minute relative volume and "close above the 10:00 high" from completed five-minute
  bars as daily evidence; a news-category catalyst rule bound to the frozen package. Recipe
  `technical.news-orb-swing@1.0.0`, tests, two reviews.

## Part 2 — VIX market gate

**Plan:** VIX below 18 → new long entries allowed; 18–22 → caution; above 22 → no new long entries (cash).

**Data:** Alpaca has no VIX index data (checked 18 Nov: `VIX` returns no bars). The VIX futures funds VIXY and VIXM have
bars from 2016 but **lose value steadily over time**, so their prices cannot be compared with the 18 / 22 levels. The
official daily VIX history is published by Cboe (a free CSV of daily open/high/low/close since 1990). Using it means a
**new data provider** and a small download (well under 1 MB), which needs your approval.

**Proposed rule (if approved):** decide day D from the VIX close of D−1 (no look-ahead): new long entries allowed below
18, none at 22 or above. **Decision needed for 18–22 ("caution"):** (a) no new entries, (b) entries at half the normal
risk, or (c) entries allowed. Thresholds are frozen as written by the user before any result is read. It would be a third gate variant next to "SPY only" and "no gate", each counted in the
trial registry, run on the same squeeze recipe. "Shorting in a storm" is a separate strategy and not part of this gate.

**Not verifiable here:** the plan's statement that VIX was around 22–23 on 17–18 Nov 2026 cannot be checked until the
Cboe data is approved and loaded.

## Known difference to raise: stop on the entry day

The user's plan attaches the stop at entry (an Alpaca bracket). The engine places a daily strategy's stop at the **close
of the fill day** and checks it from the next session, so a fall through the stop on the entry day itself is not counted
until the next session. This is an existing rule for every daily strategy (`CheckExitsFromFillDay`, `ValidateNoStopRules`).
Changing it affects all daily strategies and needs its own design; until then the squeeze results carry this caveat.
