# Automated US Equities Intraday Trading Research Design

**Status:** Working research specification  
**Date:** 20 July 2026  
**Market:** US-listed equities  
**Primary tools discussed:** Finviz Elite, Alpaca News API, Alpaca Market Data API, Alpaca Trading API  
**Purpose:** Define an automated process for candidate discovery, catalyst analysis, technical selection, execution, position monitoring, and exit management.

> This is a research and engineering design, not a claim that any strategy is profitable. Every rule, threshold, score, and trading window must be tested with timestamp-correct data, realistic spreads, slippage, partial fills, fees, and out-of-sample validation before live use.

---

## 1. Project objective

Build an automated engine that can:

1. Identify a manageable universe of liquid US equities.
2. Detect stocks experiencing abnormal activity.
3. Determine whether a fresh and material catalyst exists.
4. Separate different opportunity types rather than forcing them into one model.
5. Rank candidates using market confirmation and tradability.
6. Apply strategy-specific technical conditions.
7. Execute orders through Alpaca.
8. Monitor open positions.
9. Move or replace stops when permitted by the strategy and broker.
10. Exit on target, invalidation, time, risk, or system conditions.
11. Record every decision for later analysis.

The central working hypothesis is:

> **Screening and selection create most of the potential edge. Execution is necessary but cannot rescue a poor candidate-selection process.**

---

## 2. Current project flow

The current flow described is:

```text
Manual stock entry based on knowledge and experience
                    +
              Finviz screening
                    ↓
        Alpaca news and stock candles
                    ↓
          Technical indicator pipeline
                    ↓
             Strategy decision
                    ↓
                 Execution
```

The principal weak point is broad, repeatable candidate discovery. The proposed refinement is:

```text
Finviz discovery
      ↓
Catalyst classification
      ↓
Market-data confirmation
      ↓
Setup classification
      ↓
Technical trigger
      ↓
Risk and execution
      ↓
Position management
      ↓
Post-trade research record
```

### Responsibility boundaries

| Component | Primary responsibility |
|---|---|
| Finviz Elite | Broad discovery of unusual, liquid stocks |
| Alpaca News | Catalyst retrieval, freshness, category, materiality and novelty |
| Alpaca Market Data | Quotes, trades, bars, volume, spread and market structure |
| Strategy engine | Setup-specific technical confirmation and invalidation |
| Risk engine | Position size, loss limits, exposure and kill switches |
| Alpaca Trading API | Orders, fills, cancels, replacements and position closure |
| Research store | Inputs, decisions, outcomes, MAE/MFE, slippage and model diagnostics |

Finviz should **not** make the final trading decision. It should reduce the universe to a tractable candidate list.

---

## 3. Core design principles

### 3.1 Screening is not selection

- **Screening:** Find stocks where something unusual may be happening.
- **Selection:** Determine whether a stock matches a specific, testable trading setup.
- **Entry:** Wait for the setup's exact trigger.
- **Management:** Apply rules defined before the order is placed.

A technical indicator appearing on an inactive stock is usually less meaningful than the same condition occurring after a material catalyst with abnormal volume and adequate liquidity.

### 3.2 Information time is not trading time

A catalyst may arrive after-hours, but the best executable trade may occur during regular hours. The engine should separately model:

- when information was published;
- when the first trade occurred;
- when liquidity became sufficient;
- when the setup became valid;
- when execution was permitted.

### 3.3 Do not combine opposing strategies

Momentum continuation and reversal are different hypotheses.

A momentum candidate may require:

```text
Fresh material catalyst
+ abnormal participation
+ positive excess return
+ acceptance near session highs
+ controlled spread
```

A reversal candidate may require:

```text
Weak, stale or misunderstood catalyst
+ extreme volatility-adjusted move
+ failed continuation
+ declining imbalance
+ return inside a prior range or below VWAP
```

They should not be merged into a single formula such as:

```text
gap + RSI + MACD + volume = trade score
```

### 3.4 Keep catalyst and market confirmation separate

The news score should describe the **information event**.  
The market-confirmation score should describe **how the market is trading the event**.

Mixing gap, volume and price response into the news score would count the same market reaction more than once.

---

## 4. Tradable universe

Initial universe:

- NYSE, Nasdaq and NYSE American/AMEX-listed equities.
- Common stocks initially; exclude ETFs, funds, shell companies and other non-operating structures unless deliberately included.
- US-domiciled companies if `Country = USA` is applied.
- Leave country unrestricted if ADRs and foreign issuers listed in the US are allowed.

### Initial eligibility filters

| Variable | Initial research threshold | Reason |
|---|---:|---|
| Price | Above $10 | Avoid many penny-stock and sub-dollar microstructure effects |
| Market capitalization | Above $2 billion | Cleaner initial cohort; reduce microcap-specific risks |
| Average daily volume | Above 1 million shares | Coarse liquidity gate |
| Average daily dollar volume | Calculate internally | More comparable than share volume |
| Spread | Calculate in basis points | Direct execution-cost gate |
| Shortability | Required for short setups | Avoid selecting trades that cannot be executed |
| Data coverage | SIP preferred | IEX-only data is incomplete for whole-market intraday analysis |

These are starting values, not optimized values.

---

## 5. Finviz Elite discovery screens

Finviz defines relative volume as current volume divided by three-month average volume and states that it is adjusted intraday. It defines its standard gap field using the regular-session opening price versus the prior close. Therefore, calculate the true premarket gap directly from market data rather than assuming the standard gap field is a premarket-gap measurement.

### 5.1 Preset A: Liquid abnormal movers — bullish

| Finviz filter | Setting |
|---|---|
| Exchange | NYSE, Nasdaq, AMEX |
| Security type | Stocks; exclude funds where possible |
| Market cap | Over $2B |
| Price | Over $10 |
| Average volume | Over 1M |
| Relative volume | Over 1.5 |
| Volatility — week | Over 2% |
| Change | Over 3% |
| Signal | None |
| Sort | Relative volume descending |

### 5.2 Preset B: Liquid abnormal movers — bearish

Use the same filters, with:

```text
Change: Under -3%
```

### 5.3 Premarket discovery preset

Run after sufficient premarket activity has accumulated.

| Filter | Initial setting |
|---|---:|
| Price | Over $10 |
| Market cap | Over $2B |
| Average volume | Over 1M |
| Current volume | Over 100,000 |
| Change | Above +2% or below -2% |
| Relative volume | Above 1.5, interpreted cautiously |
| Sort | Current volume, then absolute percentage change |

Calculate internally:

```text
premarket_gap =
    latest_premarket_price / previous_regular_session_close - 1
```

Also calculate:

```text
premarket_dollar_volume =
    sum(trade_price × trade_size during premarket)
```

### 5.4 Post-open preset

A starting version for approximately 09:35–09:45 ET:

| Filter | Setting |
|---|---:|
| Price | Above $10 |
| Market cap | Above $2B |
| Average volume | Above 1M |
| Current volume | Above 500K |
| Relative volume | Above 2 |
| Change | Above +3% or below -3% |

### 5.5 Handling an overly broad Finviz result

Do **not** keep tightening Finviz until only a few symbols remain. Excessive tightening can eliminate genuine opportunities.

Use this operating guide:

| Number of results | Response |
|---:|---|
| Under 20 | Consider relaxing change or relative-volume thresholds |
| 20–80 | Good discovery range |
| 80–150 | Keep the screen broad; allow catalyst and market-data stages to rank |
| Over 150 | Tighten abnormal activity or liquidity |
| Over 300 | Likely a broad market event; emphasize excess return and sector-relative strength |

### Candidate funnel

```text
Finviz discovery:              30–150
News and catalyst analysis:    10–30
Liquidity and market structure: 5–15
Strategy-valid candidates:      1–5
Actual trades:                  0–3
```

The target is not to force a fixed number of trades. Zero trades must remain a valid output.

---

## 6. Screening schedule

Use `America/New_York` as the canonical market timezone. Do not hard-code a permanent Amsterdam offset because US and European daylight-saving transitions do not always occur on the same dates.

### Market-session workflow in Eastern Time

| Time | Engine action | Primary purpose |
|---|---|---|
| 16:05 | Closing-state capture | Record official close, closing volume, late-session structure |
| 16:30–17:00 | First after-hours catalyst scan | Detect earnings, guidance and material announcements |
| 19:45 | Final postmarket snapshot | Measure whether the first reaction held, faded or reversed |
| 04:05 | Overnight refresh | Detect new releases and early premarket trading |
| 07:00 | Main premarket discovery | Build the broad candidate list |
| 08:30–08:45 | Context refresh | Re-evaluate after scheduled macro releases and new announcements |
| 09:15–09:25 | Final pre-open ranking | Produce primary, secondary and reject lists |
| 09:30–09:35 | Opening observation | Observe auction response, spread and first range |
| 09:35–10:30 | Primary entry window | Permit opening-strategy entries |
| 10:30–11:00 | Position-management window | Manage existing trades; normally stop initiating new ones |
| 15:00–15:50 | Future secondary strategy | Separate closing-session model |

### Why multiple scans are needed

A stock can pass through several states:

```text
Headline released
→ initial after-hours spike
→ after-hours consolidation or reversal
→ premarket reassessment
→ opening auction
→ regular-hours continuation or rejection
```

A single scan cannot distinguish these states reliably.

---

## 7. Initial trading windows

### Initial operating rule

> **Observe after-hours, rank premarket, trade during regular hours.**

### 7.1 After-hours: 16:00–20:00 ET

Initial use:

- catalyst detection;
- first-reaction measurement;
- after-hours volume and spread;
- reaction high, low and close;
- hold-versus-reversal classification.

Initial recommendation: **do not automate live entries here**. Extended-hours markets generally have lower liquidity, wider spreads, greater volatility, uncertain prices and higher partial-fill risk.

### 7.2 Premarket: 04:00–09:30 ET

Initial use:

- candidate discovery;
- overnight-news refresh;
- gap and premarket-volume calculation;
- premarket trend and structure;
- spread-quality monitoring;
- survival of the after-hours thesis;
- context versus SPY, QQQ and the relevant sector ETF.

The more useful ranking period is likely around **08:45–09:25 ET**, rather than immediately after 04:00, because early prints may be sparse.

### 7.3 Primary regular-hours entry window: 09:35–10:30 ET

Starting research rule:

```text
Entries allowed: 09:35–10:30
Manage positions: until 11:00 or strategy exit
No routine new entries: after 10:30
```

Waiting for the first five minutes permits observation of:

- opening-auction response;
- initial spread normalization;
- first five-minute range;
- real regular-session volume;
- premarket-high or premarket-low acceptance;
- initial VWAP formation.

This is a controlled starting point, not an assertion that five minutes is optimal.

### 7.4 Midday

Initial exclusion window:

```text
11:30–14:30 ET
```

Allow exceptions only for a genuinely new catalyst or a separately researched midday strategy.

### 7.5 Closing window: 15:00–15:50 ET

Possible later strategy:

- trend continuation;
- high-of-day/low-of-day breakout;
- institutional closing flow;
- late news reaction;
- failed morning move.

This must be a separate model with separate validation.

---

## 8. Catalyst-scoring model

“Has news” is too crude. A useful catalyst model must evaluate the meaning and quality of the information.

### 8.1 Catalyst categories

Initial taxonomy:

| Category | Examples |
|---|---|
| Earnings | EPS, revenue, margins, cash flow |
| Guidance | Raised, lowered, withdrawn or initiated |
| Merger and acquisition | Offer, agreement, termination, regulatory decision |
| Regulatory | FDA, FTC, DOJ, SEC or other agency action |
| Capital structure | Offering, buyback, debt issuance, refinancing, dilution |
| Major commercial event | Contract, customer, partnership, order, product approval |
| Management | CEO/CFO change, departure, investigation |
| Analyst action | Upgrade, downgrade, target change, initiation |
| Legal | Lawsuit, settlement, criminal or civil action |
| Operational | Outage, cyber incident, recall, production disruption |
| Macro/sector | Commodity, rates, regulation or peer event |
| Promotional or low-information | Conference attendance, recycled release, generic corporate update |
| Unknown | Insufficient information to classify |

### 8.2 Pure catalyst score: 0–100

Keep this independent from price and volume.

| Component | Weight | Question |
|---|---:|---|
| Materiality | 25 | Could this meaningfully change revenue, earnings, cash flow, risk or valuation? |
| Novelty | 20 | Is the information genuinely new rather than a repetition? |
| Surprise magnitude | 20 | How different is the event from prior expectations? |
| Directional clarity | 15 | Is the likely economic direction reasonably clear? |
| Source reliability | 10 | Is the source a filing, issuer, regulator or established news provider? |
| Freshness | 10 | How recently was the information first published? |

```text
catalyst_score =
    materiality
  + novelty
  + surprise
  + directional_clarity
  + source_reliability
  + freshness
```

### 8.3 Penalties and exclusions

Apply explicit penalties rather than quietly lowering subjective scores.

| Condition | Suggested research penalty |
|---|---:|
| Near-duplicate/reprinted story | -20 to -40 |
| Old information republished as new | -30 to -60 |
| Unconfirmed rumor | -20 to -50 |
| Promotional release with weak economics | -10 to -30 |
| Headline conflicts with article details | Manual/LLM review or exclusion |
| Multiple contradictory reports | Mark ambiguous; no automated trade |
| Timestamp cannot be trusted | Exclude from event study |

The numbers above are research priors only and require calibration.

### 8.4 Catalyst output object

```python
CatalystResult(
    symbol="XYZ",
    first_published_at="2026-07-20T12:14:08Z",
    latest_update_at="2026-07-20T12:18:42Z",
    category="earnings_guidance",
    direction="positive",
    materiality=0.92,
    novelty=0.88,
    surprise=0.76,
    directional_clarity=0.85,
    source_reliability=0.95,
    freshness=0.97,
    catalyst_score=88.5,
    duplicate_of=None,
    explanation=[
        "Revenue above consensus",
        "Full-year guidance raised",
        "First publication timestamp verified"
    ]
)
```

### 8.5 Candidate cohorts

Do not mix these during initial research:

1. Fresh company-specific catalyst plus abnormal movement.
2. No detected catalyst plus abnormal movement.
3. Sector or macro catalyst.
4. Stale or weak catalyst.
5. Conflicting or ambiguous catalyst.
6. Corporate-action or data-quality anomaly.

Start paper testing with cohort 1. Store the others for later research.

---

## 9. Market-confirmation score

After catalyst scoring, determine whether the security is actually tradeable and whether the market confirms the event.

### 9.1 Inputs

- Premarket dollar volume.
- Same-time-of-day relative volume.
- Spread in basis points.
- Quoted depth.
- Number of trades.
- Gap normalized by ATR.
- Excess return versus SPY/QQQ.
- Excess return versus sector ETF.
- Position within premarket range.
- Holding versus rejecting the initial reaction.
- Opening-range behavior.
- VWAP relationship.
- Halt state and limit-up/limit-down status.
- Short availability and borrow conditions for short trades.

### 9.2 Same-time relative volume

Prefer:

```text
volume_so_far_today
──────────────────────────────
median volume by the same clock
time over the previous N sessions
```

For example:

```text
Volume from 09:30–09:40 today
──────────────────────────────
Median 09:30–09:40 volume over
the previous 20 sessions
```

This is more controlled than comparing early-session volume with a full-day average.

### 9.3 Excess return

Calculate broad-market and sector-relative movement:

```text
market_excess_return =
    stock_return - benchmark_return

sector_excess_return =
    stock_return - sector_ETF_return
```

When the whole market moves sharply, absolute percentage change is less informative.

### 9.4 Example market-confirmation score

This is a starting research weighting, not a validated model:

| Component | Weight |
|---|---:|
| Same-time relative volume | 25 |
| Dollar liquidity | 20 |
| Spread and depth quality | 20 |
| Sector-relative strength/weakness | 15 |
| Gap normalized by ATR | 10 |
| Hold/fade structure | 10 |

Do not include catalyst score components here.

---

## 10. Candidate ranking

An initial ranking model can combine independent scores:

```text
candidate_score =
    0.45 × catalyst_score
  + 0.35 × market_confirmation_score
  + 0.20 × strategy_setup_score
```

This is only a scaffold. Each score and interaction must be tested.

A better long-term model may rank candidates separately by strategy:

```text
momentum_rank
reversal_rank
opening_range_rank
VWAP_pullback_rank
closing_momentum_rank
```

One stock may rank highly for a reversal model and poorly for continuation.

### Reject reasons must be explicit

Examples:

```text
REJECT_WIDE_SPREAD
REJECT_LOW_DOLLAR_VOLUME
REJECT_STALE_NEWS
REJECT_AMBIGUOUS_DIRECTION
REJECT_NO_SIP_DATA
REJECT_HALT_OR_LULD
REJECT_ALREADY_EXTENDED
REJECT_NO_SHORT_AVAILABILITY
REJECT_DUPLICATE_EVENT
REJECT_SETUP_INVALID
```

A reject list is as important as a selected list for research.

---

## 11. Technical indicators and setup logic

### Principle

Technical indicators should confirm a defined setup after candidate discovery. They should not be the primary broad-market discovery tool.

Daily RSI, daily moving averages and daily ATR can describe context. Intraday decisions should be calculated from the actual intraday data used by the execution system.

### Core intraday reference levels

- Previous regular-session close.
- Previous day high and low.
- After-hours high and low.
- Premarket high and low.
- Premarket VWAP.
- Regular-session VWAP.
- Opening range: 1-, 5- or 15-minute.
- Daily ATR and intraday realized volatility.
- High of day and low of day.
- Key gap-fill levels.
- Sector and index direction.

### Initial strategy families

#### A. Catalyst momentum continuation

Possible conditions:

```text
Fresh material directional catalyst
+ adequate premarket dollar volume
+ positive/negative excess return
+ controlled spread
+ opening holds the premarket move
+ breakout or pullback trigger
```

Potential triggers:

- break and hold above premarket high;
- first pullback holds VWAP;
- opening-range breakout with abnormal volume;
- higher low followed by reclaim of a defined level.

#### B. Failed catalyst / gap reversal

Possible conditions:

```text
Weak, stale or misunderstood catalyst
+ extreme gap relative to ATR
+ failure at premarket or opening high
+ loss of VWAP
+ declining buying pressure
```

This must remain separate from momentum continuation.

#### C. Opening-range breakout

Research dimensions:

- 1-, 5- and 15-minute ranges;
- breakout on close versus intrabar break;
- volume confirmation;
- maximum acceptable distance from VWAP;
- market and sector alignment;
- retest versus immediate entry.

#### D. VWAP pullback

Research dimensions:

- first versus later VWAP test;
- slope of VWAP;
- volume contraction on pullback;
- price acceptance above/below VWAP;
- catalyst strength;
- proximity to premarket high or opening range.

#### E. Closing momentum

Add only after the opening system is independently validated.

### Indicators to use cautiously

- RSI.
- MACD.
- generic moving-average crosses.
- stochastic oscillators.
- static overbought/oversold thresholds.

These can be useful features, but should not be assumed to provide edge without event, regime and execution context.

---

## 12. Why the approach may work

These are empirical notions and research hypotheses, not guaranteed trading rules.

### 12.1 Information is not always incorporated instantly

Post-earnings-announcement drift is a documented tendency for abnormal returns to continue in the direction of an earnings surprise over subsequent weeks. It supports the general idea that information processing can be gradual, but it does **not** directly prove a profitable intraday strategy.

### 12.2 Intraday momentum exists in some market contexts

Research using the S&P 500 ETF found that the first half-hour market return predicted the final half-hour return, with stronger effects on high-volume, high-volatility and major-news days. This is evidence for a market-level pattern, not automatic proof for individual-stock scalping.

### 12.3 Stale news can produce reversal

Research found that investors react less to stale news, but returns on stale-news days can negatively predict subsequent returns, with stronger reversal where individual-investor activity is elevated. This supports explicitly measuring news novelty and duplicate similarity.

### 12.4 Order flow can persist

Research has attributed short-horizon order-flow persistence substantially to large orders being split into smaller executions. This provides a possible mechanism for continuation after a material catalyst, although public data cannot perfectly identify the underlying meta-order.

### Practical interpretation

The engine should test:

```text
fresh information + persistent participation
```

against:

```text
stale information + temporary attention shock
```

They may produce opposite strategies.

---

## 13. Market-data requirements

### 13.1 SIP versus IEX

Alpaca's free real-time equities feed may provide IEX-only data, while the SIP feed covers consolidated US exchange reporting. For whole-market screening and short-horizon spread/volume analysis, IEX-only data can materially understate total activity.

The system should record:

```python
data_feed = "sip"  # or "iex"
```

Do not combine training samples from different feeds without marking the difference.

For serious scalping research, evaluate whether consolidated SIP data is required for:

- total volume;
- national best bid and offer;
- quote depth and spread;
- exchange-wide trades;
- opening behavior;
- same-time relative volume.

### 13.2 Required event timestamps

Store at least:

- source publication timestamp;
- first ingestion timestamp;
- update timestamp;
- first market-data timestamp after publication;
- decision timestamp;
- order-submission timestamp;
- broker-acknowledgement timestamp;
- fill timestamps;
- cancel/replace timestamps.

Without correct timestamps, news backtesting is vulnerable to look-ahead bias.

### 13.3 Real-time streams

Use WebSocket streams for:

- trades;
- quotes;
- minute bars;
- news;
- account and order updates.

Polling alone may create unnecessary latency and inconsistent state.

---

## 14. Execution and position management

Execution may be technically solved, but the surrounding controls remain critical.

### Entry controls

Before submitting:

- candidate remains valid;
- quote is recent;
- spread below maximum;
- no halt or limit-state condition;
- position size within risk limit;
- total exposure within limit;
- no duplicate open order;
- no conflicting position;
- expected slippage below limit;
- short remains available if required.

### Position sizing

Define risk in dollars or basis points of equity:

```text
shares =
    maximum_trade_risk_dollars
    ──────────────────────────
    entry_price - stop_price
```

Then cap by:

- maximum notional;
- percentage of recent dollar volume;
- available buying power;
- portfolio exposure;
- liquidity and spread.

### Stop management

Possible stop types:

- fixed structural stop;
- ATR/volatility stop;
- opening-range invalidation;
- VWAP invalidation;
- time stop;
- trailing stop after defined profit;
- strategy state transition.

Trailing stops require special care. A triggered stop may execute at a worse price in fast markets, and broker support may differ for trailing stops inside bracket/OCO structures.

### Exit reasons

```text
TARGET_REACHED
STRUCTURE_INVALIDATED
VWAP_FAILURE
TRAILING_STOP
TIME_EXIT
MAX_HOLD_TIME
SPREAD_EXPANSION
NEWS_REVERSAL
MARKET_REGIME_CHANGE
DATA_STALE
BROKER_ERROR
DAILY_RISK_LIMIT
SYSTEM_KILL_SWITCH
```

### Kill switches

Minimum controls:

- maximum daily loss;
- maximum loss per trade;
- maximum consecutive losses;
- maximum number of simultaneous positions;
- maximum gross and net exposure;
- stale market-data detection;
- news-stream failure;
- order-update-stream failure;
- repeated order rejection;
- unexpected open position;
- clock drift;
- broker/account mismatch;
- market-wide halt;
- manual emergency stop.

---

## 15. Suggested system architecture

```text
                 ┌─────────────────┐
                 │ Scheduler       │
                 │ New York time   │
                 └────────┬────────┘
                          │
          ┌───────────────┼────────────────┐
          │               │                │
 ┌────────▼───────┐ ┌─────▼────────┐ ┌────▼───────────┐
 │ Finviz ingest  │ │ Alpaca news  │ │ Market stream │
 │ and presets    │ │ and archive  │ │ quotes/trades │
 └────────┬───────┘ └─────┬────────┘ └────┬───────────┘
          │               │                │
          └───────────────┼────────────────┘
                          ▼
                ┌───────────────────┐
                │ Candidate store   │
                │ event-time state  │
                └─────────┬─────────┘
                          ▼
                ┌───────────────────┐
                │ Catalyst engine   │
                │ novelty/category  │
                └─────────┬─────────┘
                          ▼
                ┌───────────────────┐
                │ Market confirm.   │
                │ liquidity/RVOL    │
                └─────────┬─────────┘
                          ▼
                ┌───────────────────┐
                │ Strategy engine   │
                │ setup and trigger │
                └─────────┬─────────┘
                          ▼
                ┌───────────────────┐
                │ Risk engine       │
                │ sizing/limits     │
                └─────────┬─────────┘
                          ▼
                ┌───────────────────┐
                │ Order manager     │
                │ state/idempotency │
                └─────────┬─────────┘
                          ▼
                ┌───────────────────┐
                │ Position monitor  │
                │ stops/exits       │
                └─────────┬─────────┘
                          ▼
                ┌───────────────────┐
                │ Research journal  │
                │ features/outcomes │
                └───────────────────┘
```

### Candidate state machine

```text
DISCOVERED
→ NEWS_PENDING
→ CATALYST_CLASSIFIED
→ MARKET_DATA_PENDING
→ RANKED
→ WATCHING
→ SETUP_VALID
→ ORDER_PENDING
→ PARTIALLY_FILLED / FILLED
→ MANAGING
→ EXIT_PENDING
→ CLOSED
```

Terminal non-trade states:

```text
REJECTED
EXPIRED
DATA_ERROR
RISK_BLOCKED
MANUAL_BLOCK
```

Every transition should include timestamp and reason.

---

## 16. Candidate data model

```python
@dataclass
class Candidate:
    symbol: str
    discovered_at: datetime
    discovery_source: str
    finviz_preset: str

    previous_close: float | None
    last_price: float | None
    gap_pct: float | None
    gap_atr: float | None

    premarket_volume: int | None
    premarket_dollar_volume: float | None
    same_time_rvol: float | None
    spread_bps: float | None
    quote_age_ms: int | None

    benchmark_return: float | None
    sector_return: float | None
    market_excess_return: float | None
    sector_excess_return: float | None

    catalyst: CatalystResult | None
    market_confirmation_score: float | None
    setup_scores: dict[str, float]

    selected_strategy: str | None
    state: str
    reject_reasons: list[str]
```

---

## 17. Research and backtesting methodology

### 17.1 First objective: validate selection, not P&L alone

Measure whether the screening pipeline identifies stocks with tradeable movement after the selection timestamp.

Possible labels:

- maximum favorable excursion after selection;
- maximum adverse excursion;
- return after 5, 15, 30 and 60 minutes;
- probability of touching +1R before -1R;
- probability of breaking premarket high/low;
- probability of VWAP hold or failure;
- spread and slippage after selection;
- fill probability at intended order type.

### 17.2 Required bias controls

- No look-ahead news fields.
- Use first-publication time, not a later updated article time.
- Survivorship-bias-free symbol universe.
- Corporate-action adjustment.
- Delisted and acquired stocks retained where applicable.
- Correct session boundaries.
- Correct daylight-saving handling.
- Halt and limit-up/limit-down handling.
- Realistic bid/ask execution.
- Partial fills.
- Latency assumptions.
- Cancel/replace latency.
- Distinguish SIP and IEX data.
- Do not use final daily volume in an intraday decision.
- Do not use a revised financial figure that was unavailable at decision time.

### 17.3 Validation split

Prefer time-based evaluation:

```text
Development period
→ validation period
→ untouched test period
→ walk-forward paper trading
→ small controlled live deployment
```

Do not randomly shuffle event-driven time-series data without considering regime leakage.

### 17.4 Metrics

#### Candidate-selection metrics

- candidates per day;
- percentage with fresh catalysts;
- category distribution;
- false duplicate rate;
- catalyst-classification accuracy;
- selected-versus-rejected outcome distribution;
- average post-selection spread;
- average same-time RVOL.

#### Trading metrics

- expectancy per trade;
- win rate;
- average win and loss;
- profit factor;
- maximum drawdown;
- Sharpe/Sortino where appropriate;
- return per unit of risk;
- maximum adverse excursion;
- maximum favorable excursion;
- time in trade;
- slippage;
- fill ratio;
- cancellation ratio;
- rejected-order rate;
- performance by catalyst category;
- performance by time window;
- performance by market regime.

#### Stability metrics

- month-by-month results;
- threshold sensitivity;
- performance with doubled slippage;
- performance after removing top outliers;
- performance by market-cap and liquidity bucket;
- performance on high- and low-volatility days.

---

## 18. Recommended implementation phases

### Phase 0 — Passive data capture

No trading.

Capture:

- Finviz candidates;
- all news and timestamps;
- quotes, trades and bars;
- candidate scores;
- hypothetical signals;
- future outcomes.

Goal: prove data quality and event-time correctness.

### Phase 1 — Screening and ranking

Deliver daily:

- primary watchlist;
- secondary watchlist;
- rejected candidates with reasons;
- catalyst explanation;
- market-confirmation explanation.

No automated orders.

### Phase 2 — Paper trading

Enable:

- strategy triggers;
- position sizing;
- simulated order management;
- stop and exit management;
- full audit log.

Evaluate differences between theoretical and broker paper fills.

### Phase 3 — Controlled live execution

Use:

- small risk;
- strict daily loss limit;
- limited number of strategies;
- limited time window;
- manual kill switch;
- no extended-hours execution initially.

### Phase 4 — Expansion

Only after independent evidence:

- premarket execution;
- after-hours execution;
- closing strategy;
- reversal models;
- non-news momentum;
- lower-market-cap cohorts;
- futures or options as separate instruments.

---

## 19. Recommended first MVP

The first researchable system should be narrow:

### Universe

```text
US common stocks
price > $10
market cap > $2B
average volume > 1M
```

### Event cohort

```text
Fresh company-specific earnings or guidance catalyst
```

### Timing

```text
Discover after-hours or premarket
Rank at 09:15–09:25 ET
Observe 09:30–09:35 ET
Enter only 09:35–10:30 ET
```

### Strategy

Choose one:

```text
5-minute opening-range continuation
```

or:

```text
first VWAP pullback after a confirmed catalyst
```

Do not test both as one strategy.

### Data

```text
SIP quotes/trades preferred
Alpaca news with first-seen timestamp
1-minute bars plus quote stream
```

### Risk

```text
One open position initially
fixed risk per trade
maximum daily loss
hard stale-data and order-state kill switches
```

### Output

Every candidate should answer:

1. Why was it discovered?
2. What is the catalyst?
3. Is the catalyst fresh and material?
4. Is the market confirming it?
5. Which setup is being tested?
6. What invalidates the setup?
7. What is the expected spread and slippage?
8. Why was it traded or rejected?
9. What happened after the decision timestamp?

---

## 20. Open design decisions

These should be resolved through data rather than intuition:

- Minimum market capitalization.
- Minimum average and current dollar volume.
- Maximum acceptable spread.
- Minimum same-time RVOL.
- Minimum catalyst score.
- Optimal first observation period: 1, 3, 5 or 15 minutes.
- Opening-range definition.
- Entry on breakout, close, retest or pullback.
- Stop based on structure, ATR or fixed risk.
- Partial profit-taking versus single exit.
- Maximum holding time.
- Whether to trade against broad-market direction.
- Whether analyst actions are strong enough as standalone catalysts.
- How to handle multiple related headlines.
- Whether premarket price action improves or weakens prediction.
- Whether extended-hours execution adds value after costs.
- Whether the SIP subscription materially improves results relative to IEX-only data.

---

## 21. Immediate next engineering tasks

1. Create saved Finviz bullish and bearish discovery presets.
2. Add scheduled scans for 16:30, 19:45, 07:00, 08:45 and 09:20 ET.
3. Ingest candidates into a timestamped candidate table.
4. Retrieve Alpaca news from the previous regular close through the decision time.
5. Deduplicate stories using canonical URL, headline similarity and body similarity.
6. Implement catalyst category, novelty and materiality fields.
7. Calculate premarket gap directly from prior regular close.
8. Add same-time relative volume.
9. Add spread in basis points and quote-age checks.
10. Add SPY/QQQ and sector-relative returns.
11. Produce a ranked watchlist and a reject list.
12. Capture 09:30–11:00 outcomes without trading.
13. Select one opening setup for paper testing.
14. Add complete order-state and risk-control logging before live execution.

---

## 22. Source references

1. **Finviz Screener Help** — definitions of exchange coverage, gap, ATR, average volume, current volume and intraday-adjusted relative volume.  
   https://finviz.com/help/screener

2. **Finviz Elite / FAQ** — real-time Elite data, premarket 04:00–09:30 ET, after-hours 16:00–20:00 ET, export/API capability.  
   https://finviz.com/help/faq  
   https://finviz.com/elite

3. **Alpaca Historical News Data** — historical news, Benzinga sourcing and real-time news use cases.  
   https://docs.alpaca.markets/us/docs/historical-news-data

4. **Alpaca Real-time Stock Data** — WebSocket trades, quotes, bars and extended-hours aggregation.  
   https://docs.alpaca.markets/us/v1.4.2/docs/real-time-stock-pricing-data

5. **Alpaca Market Data FAQ** — IEX versus SIP coverage and subscription distinctions.  
   https://docs.alpaca.markets/us/docs/market-data-faq

6. **Alpaca Orders** — trailing-stop behavior and execution-price risk.  
   https://docs.alpaca.markets/us/docs/orders-at-alpaca

7. **FINRA Rule 2265** — lower liquidity, higher volatility, changing prices and partial/non-execution risks in extended hours.  
   https://www.finra.org/rules-guidance/rulebooks/finra-rules/2265

8. **FINRA: Extended-Hours Trading—Know the Risks** — regular hours and extended-hours execution risks.  
   https://www.finra.org/investors/insights/extended-hours-trading

9. **SEC: After-Hours Trading—Understanding the Risks** — wider spreads, volatility, lower volume and uncertain prices.  
   https://www.sec.gov/about/reports-publications/investorpubsafterhourshtm

10. **Gao, Han, Li and Zhou — Intraday Momentum** — first-half-hour market return predicting the last-half-hour return in the studied sample.  
    https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2552752

11. **Livnat and Mendenhall — Post-Earnings-Announcement Drift** — drift following earnings surprises.  
    https://doi.org/10.1111/j.1475-679X.2006.00196.x

12. **Tetlock — All the News That's Fit to Reprint** — stale-news measurement and subsequent return reversal.  
    https://doi.org/10.1093/rfs/hhq141

13. **Tóth, Palit, Lillo and Farmer — Why Is Order Flow So Persistent?** — evidence relating short-horizon order-flow persistence to order splitting.  
    https://arxiv.org/abs/1108.1632

---

## 23. Summary

The recommended first system is not an all-purpose autonomous trader. It is a controlled research pipeline:

```text
Discover broadly with Finviz
→ verify and score fresh catalysts
→ confirm liquidity and abnormal participation
→ classify the setup
→ wait for a precise regular-hours trigger
→ apply independent risk controls
→ log every decision and outcome
```

The strongest initial research scope is:

```text
Fresh earnings/guidance catalyst
+ liquid US stock
+ abnormal premarket activity
+ 09:35–10:30 ET regular-hours entry window
+ one clearly defined continuation setup
```

After this cohort is validated, expand one dimension at a time.
