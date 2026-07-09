# Strategy Design Doctrine — Gates, Triggers, and Catalysts (2026-07)

Research review and design plan. No code — this document defines *how strategies should
be constructed* before any further rule is implemented. Companion to
`docs/edge-recovery-master-plan.md` (which fixes the *measurement*; this fixes the
*design*).

---

## 1. The Central Diagnosis — Proven by Your Own Ledger

Re-read `docs/strategy-last-runs.md` with one question: *which strategies have an
entry that is a discrete price EVENT at a defined level, vs. a stack of indicator
STATES that happen to all be true on some bar?*

| Strategy | Entry type | Honest-rescore result (point-in-time universe + realism + promotion gate) |
|---|---|---|
| Minervini V4 Trend Rider | **Event**: breakout of 20-bar high after 25-bar volatility contraction | **+24.73%, ELIGIBLE** — 48 trades, 1.94% DD, OOS +2.12%, beats SPY +18.19%, walk-forward 5/6 |
| Reversal Reclaim Bull Quality | **Event**: 6–45% pullback, then reclaim close above SMA10/20 | **−1.44%, REJECTED** — sound trigger, but did not generalize off its cherry-picked basket (15 trades, WF 2/6) |
| Intraday V1 (TOP1) | **State stack**: EMA10>EMA20 ∧ MACD+ ∧ RVOL≥2 | **−1.10%, REJECTED** on the volatile five (was ~flat +0.09% on its original hand-picked basket) |

The pattern still holds where it matters: **the one strategy that survives honest
measurement (V4) is event-triggered, and every state-stack entry fails.** But the honest
re-score sharpens the lesson rather than leaving a clean split — Reversal Reclaim has a
sound *event* trigger yet still failed, because it was promoted on a cherry-picked basket
and did not generalize to a point-in-time universe. So the rule is two-sided:

- **Event-triggered entry design is necessary.** A state stack cannot distinguish *early*
  from *late* — it fires on the first bar all conditions coincide, which for momentum
  conditions (cumulative RVOL ≥ 2×, MACD already positive) is structurally *after* the
  move is underway (your forensics: "winners entered early on the first expansion; losers
  entered after extension"). That rules state stacks out.
- **But a good trigger is not sufficient.** To become a *promotable* edge it also needs an
  honest universe, a regime gate, and a real (≥30-trade, out-of-sample) sample. Reclaim
  had the trigger and lacked the rest; V4 had both.

**Doctrine #1: an entry is a one-shot event at a pre-defined price level (necessary) —
indicators never fire entries, they only permit them; promotion additionally requires an
honest universe, regime context, and ≥30-trade out-of-sample confirmation (sufficient).**

---

## 2. The Five-Layer Decision Stack

Every strategy must place each condition in exactly one layer. Conditions in the wrong
layer are the root of the endless-version treadmill.

```
L1  UNIVERSE   (daily, Finviz)     "Which stocks am I allowed to look at?"
L2  REGIME     (market-level)      "Is this strategy switched on today at all?"
L3  SETUP GATE (stock, slow)       "Is this stock a candidate right now?"
L4  TRIGGER    (stock, event)      "Buy NOW at this level — one shot."
L5  RISK       (sizing/exits)      "How much, where's the stop, how do I leave?"
```

- **L1 Universe** — liquidity, price, market cap, sector. Finviz Elite. Never edits
  timing.
- **L2 Regime** — SPY/QQQ above 50dma, sector ETF trend, VIX band. Without it every
  swing result is indistinguishable from sector beta — your MSFT/MU losses were this.
  **IMPLEMENTED (2026-07-06)** as an opt-in, no-lookahead entry gate. Add to any
  strategy YAML:
  ```yaml
  regime:
    benchmark: SPY            # any tradable symbol (SPY, QQQ, a sector ETF)
    rule: price_above_sma     # benchmark's prior close above its N-day SMA
    sma_period: 50            # 50 for trend archetypes, 200 for mean-reversion
  ```
  Regime membership per day is computed only from benchmark daily bars dated *before*
  that day (`RegimeCalendar`), and new entries are dropped on regime-off days. The same
  no-lookahead gate runs in **backtest and live/paper** via a shared `RegimeGateService`
  (backtest builds the full calendar; live uses `IsRegimeOnAsOf(today)` and fails closed
  if the benchmark can't be confirmed). Exits are never gated — open positions are managed
  in any regime. Benchmark daily bars are resampled from a finer timeframe when a cache
  lacks native daily. Strategies without a `regime:` block behave exactly as before.
- **L3 Setup gates** — *few, independent, slow-moving*. "Daily uptrend intact"
  (one concept — not five separate EMA booleans), "RSI zone sane", "catalyst fresh /
  absent (per archetype)", "not over-extended vs ATR". Rule of thumb: **≤ 4 setup
  gates per strategy.** Your configs carry 12–20 active booleans, many measuring the
  same thing (price>EMA10 ∧ price>EMA20 ∧ EMA10>EMA20 ∧ price>BBmid ≈ one condition:
  "short-term uptrend"). Correlated gates don't add edge; they add curve-fitting
  degrees of freedom.
- **L4 Trigger** — a *price event at a level known in advance*: break of pivot/ORH,
  reclaim of SMA/VWAP after a defined flush, first close above prior-day high after an
  oversold stretch, gap-and-go through premarket high. It happens once and is consumed
  (your `max_entries_per_ticker_per_day: 1` gestures at this; the catalyst lifecycle in
  Phase 1 formalizes it for news).
- **L5 Risk** — ATR-based stop distance, R-multiple targets, trails. ATR belongs
  *only* here.

### Indicator placement table (answers "what gates, what decides")

| Indicator | Correct layer | Correct use | Incorrect use (common in current configs) |
|---|---|---|---|
| **RSI(14)** | L3 gate (veto) | Sanity band (e.g. 45–85 for breakouts: not dead, not parabolic) | As a buy reason. RSI(14)<30 "buy oversold" is one of the weakest tested signals in liquid equities |
| **RSI(2/3)** | L3 gate (setup) | <10 in a stock above 200dma = mean-reversion *setup* (Connors) — still needs an L4 trigger | Acting on it directly without a reclaim trigger |
| **EMA/SMA alignment** | L3 gate | One "trend intact" concept per timeframe | Five separate booleans of the same fact |
| **200dma** | L2/L3 gate | The momentum/mean-reversion regime switch (above: buy dips; below: don't) | Absent everywhere today |
| **VWAP (session)** | L4 trigger material + L5 exit | *Reclaim/loss of* VWAP is an event; distance-from-VWAP caps extension | Raw "price>VWAP" state as an entry vote; instant VWAP-touch exits (853 exits in one run) |
| **Anchored VWAP** | L3/L4 | Level for pullback triggers (already in reclaim config — good) | — |
| **MACD histogram** | L3 gate (veto) | "not bearish" permission | Positive histogram as an entry vote — it's late by construction |
| **RVOL (cumulative/slot)** | L4 confirmation | Measured *at the trigger bar*: "is real participation behind this event?" | Hard state gate that delays entry until the move is old |
| **ATR** | L5 only | Stop distance, extension caps, sizing | — |
| **Bollinger** | L3 | Contraction (squeeze) as setup; lower-band tag as MR stretch marker | Band position as entry vote |
| **Catalyst/news** | Archetype-dependent (see §3) | Required+fresh (PEAD), origin-lookback (trend), **veto** (mean reversion) | Attached state tradable every bar (the catalyst-churn failure) |

---

## 3. Catalyst Doctrine — Does Swing Always Need a Catalyst?

**No.** There are three research-distinct swing families, each with a *different*
catalyst relationship. Forcing one relationship onto all three is why the catalyst
configs failed in both directions (a required-fresh rule on a trend strategy → 0 trades;
an attached-state rule re-tradable every bar → churn).

### 3a. Post-catalyst drift (catalyst **required**, and it starts the clock)
Academic base: **post-earnings announcement drift** — Ball & Brown (1968), Bernard &
Thomas (1989/90): prices under-react to earnings surprises and drift for weeks. Same
mechanism documented for guidance raises, FDA decisions, major contracts. The
practitioner version is Qullamaggie's *episodic pivot*: dormant stock + genuine
surprise + huge gap/volume → buy the first technical confirmation, hold days-weeks.
**Your own event study (984 observations) reproduced this**: positive/new news +
technical confirmation → favorable 1–5d forward returns. The edge exists in your data;
only the execution model (one-shot lifecycle, Phase 1) is missing.
- Catalyst role: **setup + clock**. Entry trigger is technical (ORH break / EMA10×20
  flip after `firstConfirmableTimestamp`), evaluated **once** within a bounded window.

### 3b. Trend continuation / breakout (catalyst **optional**, usually weeks old)
Minervini/Weinstein stage-2: the catalyst typically *originated* the move weeks ago;
at entry you need structure — tight consolidation near highs, relative strength,
volume dry-up then expansion. Requiring *fresh* news here is provably wrong (the
required-fresh trend config took 0 trades). Fresh-news at a breakout is at most a small
quality bonus.
- Catalyst role: **origin lookback** (optional confirmation that a real catalyst
  started the base), never an entry condition.

### 3c. Mean reversion (catalyst is a **VETO** — the inverse role)
The key study: **Chan (2003, Journal of Finance, "Stock Price Reaction to News and
No-News")** — price moves *without* identifiable news **reverse**; price moves *with*
news **drift** (continue). So for buying weakness: a big down move on **no fresh
negative catalyst** in an intact uptrend is a reversion candidate; the same move on
bad news is a falling knife. **This is your structural advantage** — you already run a
news pipeline that can make the news/no-news distinction cheaply, which naive dip
buyers cannot.
- Catalyst role: **absence required** (veto fresh negative news; treat fresh news of
  any kind as disqualifying for the reversion archetype).

---

## 4. "Down Today → Up Tomorrow" — What the Research Actually Says

The raw effect is real but not tradable naively:

- **Short-term reversal**: Jegadeesh (1990), Lehmann (1990) — losers over the past
  week/month outperform over the next. But profits concentrate in small/illiquid names
  and largely represent **bid-ask bounce**; post-decimalization and after costs, naive
  "buy today's biggest losers" is dead for a retail-latency system.
- **Why any of it works — liquidity provision**: Nagel (2012) shows reversal profits
  are compensation for absorbing impatient selling; the effect is strongest when
  volatility is elevated (but not crashing). You are being *paid to provide liquidity*,
  not discovering mispricing.
- **The conditions that make it tradable** (each is a layer from §2):
  1. **No-news filter** (Chan 2003, §3c) — only no-news drops reverse. Your
     differentiator.
  2. **Long-term uptrend context** — reversion buys work in stocks **above the
     200dma** (Connors' RSI(2) research popularized this: RSI(2)<10 above the 200dma
     had strong 1–5d forward expectancy; the same signal below the 200dma loses).
     "Buy weakness in strength, never weakness in weakness."
  3. **A real stretch, not one red day** — 3+ consecutive down closes, RSI(2)<10, or a
     close below the lower Bollinger while the daily trend is intact. One down day has
     near-zero signal.
  4. **An L4 trigger, not a hope** — first close back above the prior day's high, or
     intraday reclaim of the prior close / VWAP. Buying the close of the down day is
     acceptable *only* with the overnight-effect variant below.
  5. **Overnight bias**: a large share of reversal accrues **close→open** (the
     overnight drift literature; gap-down-in-uptrend tends to mean-revert at the
     open). Hold horizon is 1–5 days, target = mean (prior high / 5-day MA), not a
     trend ride.
- **Intraday version**: "it's down a lot today, buy it" is unreliable intraday. The
  researched intraday reversion shape is **capitulation flush + reclaim** — volume
  spike into a low, then reclaim of VWAP/level — which is an event trigger, and is
  exactly the shape of your one profitable reclaim strategy, just on a shorter clock.

**Verdict**: build this as *Archetype C* (§6) — a swing (1–5 day) strategy, not an
intraday one, with the no-news veto as its identity.

---

## 5. Finviz Elite Screeners (L1 pools)

Structural screens for live/paper (per `docs/universe-finviz-screening.md`: for
*backtests* keep pools structural to avoid importing hindsight; for *live scanning*
outcome filters like "top gainers" are legitimate — that IS the trade).

**A. Swing trend/VCP pool** (feeds Minervini V4 + breakout research)
```
f=cap_smallover,sh_price_o10,sh_avgvol_o500,ta_sma200_pa,ta_sma50_pa,ta_highlow52w_a70h,geo_usa
```
Liquid, above 50/200dma, within 30% of 52-week high. Optional quality add-on:
`fa_epsqoq_o15` (EPS growth) for a Minervini-faithful variant.

**B. Episodic pivot / PEAD daily scan** (feeds Archetype B; run premarket, live only)
```
f=earningsdate_todaybefore|yesterdayafter,ta_gap_u4,sh_relvol_o2,sh_avgvol_o300,sh_price_o5,geo_usa
```
Earnings just reported + gap ≥4% + RVOL ≥2. This is an *outcome* screen by design —
correct for live event trading, not for backtest pools.

**C. Mean-reversion-in-uptrend pool** (feeds Archetype C)
```
f=ta_sma200_pa,sh_avgvol_o1000,sh_price_o10,cap_midover,geo_usa&ft=ta_rsi_os40
```
Liquid mid/large caps above the 200dma that are short-term weak (RSI<40). Your
pipeline then applies the *no-fresh-negative-news* veto and the 3-day-stretch +
reclaim trigger — the parts Finviz cannot express.

**D. Intraday momentum premarket scan** (only if intraday is retained)
```
f=ta_gap_u5,sh_relvol_o3,sh_price_5to50,sh_float_u50,news_date_today,geo_usa
```
Gap ≥5%, RVOL ≥3, tradable price band, low float, with news today — the classic
gap-and-go pool. Elite's premarket data and float filters matter here.

---

## 6. The Four Archetypes (full layer specs)

### A — Swing Trend Continuation (exists: Minervini V4; refine, don't rebuild)
| Layer | Spec |
|---|---|
| L1 | Screen A |
| L2 | **ADD**: SPY or sector ETF > 50dma, else no new entries |
| L3 | Daily uptrend intact (one alignment concept), RSI(14) 45–85, 25-bar contraction present |
| L4 | Breakout close above the contraction pivot (already the design) |
| L5 | 2.5 ATR stop, trail after 2R, exit on confirmed (2-close) loss of EMA20 — not single-touch |
| Catalyst | Optional origin bonus (catalyst within lookback that started the base); never required |
| First action | Re-score honestly (Phase 0 universe + realism + promotion gate). It was promoted on cherry-picked semis; get its true baseline before refining. |

### B — Swing Post-Catalyst Drift (the event study, finally executable)
| Layer | Spec |
|---|---|
| L1 | Screen B live; historical catalyst archive for research |
| L2 | Market not in crash regime (SPY > 50dma or VIX band) |
| L3 | Catalyst fresh (received-time), novel (dedupe), category ∈ {earnings/guidance, positive general news}; price move sane (0.25–12% — these catalyst move-size bounds tested well) |
| L4 | **One-shot**: at first confirmable candle + bounded window (2–6 bars of 15m/1h), require confirmation (EMA10×20 flip or MACD turn + volume expansion) → enter or consume the catalyst. Never re-evaluate. |
| L5 | 1.5 ATR stop, 3R target, 3–5 day max hold, `allow_same_bar_stop_target: false` |
| Catalyst | **Required + is the clock** — depends on Phase 1 lifecycle (edge-recovery plan) |
| First action | Build Phase 1 lifecycle, then translate the event-study buckets exactly once. |

### C — Swing Mean Reversion in Uptrend (**new** — the "down today, up tomorrow" answer)
| Layer | Spec |
|---|---|
| L1 | Screen C (liquid, above 200dma) |
| L2 | SPY above 200dma (reversion in bull regimes; Nagel: elevated-but-not-crash vol is the sweet spot) |
| L3 | Stretch: ≥3 consecutive down closes **or** RSI(2)<10 **or** close < lower Bollinger, with daily uptrend intact; **VETO any fresh negative catalyst (<48h)** — Chan's no-news condition, your unique capability |
| L4 | First close back above prior-day high, or intraday reclaim of prior close (event, one shot) |
| L5 | Stop below the stretch low; target = 5-day MA / prior high (mean, not trend); time-stop 5 days; no trailing |
| Catalyst | **Absence required** (veto role) |
| First action | Cheapest research on the shelf: run the existing event-study runner *inverted* — measure 1–5d forward returns after 3-down-day stretches, split by news vs no-news and above/below 200dma. If your data reproduces Chan, the rule follows directly. |

### D — Intraday (deprioritized until A–C prove out)
Your own July-5 finding: the target names move on weekly horizons; same-session
systems are structurally mismatched, and intraday is where every experiment died.
If retained: only two research-supported shapes, both event-triggered — (1) gap+catalyst
opening-range breakout from Screen D with premarket-high trigger; (2) capitulation
flush + VWAP reclaim. No more indicator-stack variants — the ledger shows that lane is
exhausted.

---

## 7. Why It Kept Failing — Consolidated

1. **State stacks instead of event triggers** (§1) — the perfect split in your own ledger.
2. **Gate inflation**: 12–20 correlated booleans per config; each version toggles a few
   → curve-fitting on 10–44-trade samples. Doctrine: ≤4 independent setup gates.
3. **Hindsight baskets** until Phase 0 — every pre-Phase-0 "success" is unproven;
   iterating against fake baselines optimized noise.
4. **Horizon mismatch** — intraday rules asked to capture weekly moves.
5. **No regime layer** — long-only, always-on, in any tape.
6. **Catalyst as state** — the one edge your research proved was executed in the one
   way research says fails.
7. **Tiny-sample judgment** — promotion/retirement on <50 trades without expectancy
   confidence bounds (now blocked by the promotion gate).

---

## 8. Execution Order (research first, code later)

1. **C-research** (days, no new infra): inverted event study — stretch/no-news/200dma
   splits on your cached daily data. Cheapest possible validation of a new archetype.
2. **A-rescore** (Phase 0 already built): honest universe + realism + promotion gate on
   Minervini V4 and Reversal Reclaim → true baselines.
3. **B-build** (Phase 1 of edge-recovery plan): catalyst lifecycle → one-shot PEAD/EP
   rule from the study buckets.
4. **Regime layer** (Phase 2.1): unlocks honest versions of all three.
5. **D last, if at all** — only as event-triggered rebuilds, judged by the promotion
   gate like everything else.

Success metric unchanged from the master plan: positive on a point-in-time universe,
out-of-sample, after realistic costs, ≥30 trades, no single ticker carrying it.
