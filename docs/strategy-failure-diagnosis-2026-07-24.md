# Strategy Failure Diagnosis and Plan (2026-07-24)

Strategy-level research summary. Sources: `docs/strategy-last-runs.md` (through the
2026-07-24 audits), `docs/architecture-and-review.md`, `docs/strategy-design-doctrine.md`,
and `docs/edge-recovery-master-plan.md`. This document diagnoses why strategies fail
and sequences the next work by information gained.

## State of Play

The system's measurement is materially better, but it is not yet complete. Phase 0
(point-in-time universe evidence, execution realism, promotion gates, and universe
eligibility validation) and the doctrine (event triggers, no more than four gates,
regime layer, and one-shot catalyst lifecycle) are in place. Current code fails
promotion closed when historical universe evidence is missing.

**No strategy currently satisfies the corrected production-promotion standard.**
The strongest retained result is a research control that must be rerun under the
current risk and universe semantics:

| Strategy | Status | Evidence |
|---|---|---|
| Minervini Trend Rider V4 | **STALE RESEARCH CONTROL - REVALIDATION REQUIRED** | Historical +21.90%, 1.98% DD, 46 trades, OOS +2.48%, walk-forward 5/6. The result predates corrected risk semantics and used a static candidate universe without historical membership evidence. |

Everything else is rejected or unproven for five distinct reasons.

## The Five Real Causes of Failure

### 1. The former stop-width policy conflated two risk meanings

The first 2026-07-24 unified audit capped loss as a percentage of deployed
notional. Daily structural/ATR stops of 3.32-9.46% therefore rejected 2,351 of
2,394 candidates. That policy was subsequently rejected because it conflated
stop distance with account risk.

The resolved contract preserves the strategy stop, caps planned loss as a
percentage of current account equity, and separately caps position notional.
Quantity absorbs stop width. Results produced under the former
deployed-notional rule remain historical diagnostics and are not promotion
evidence.

### 2. Concentration masquerading as edge

- Minervini Proxy V5: six target hits supplied all aggregate profit; all five holdout
  trades lost.
- VCP V7: one DELL trade contributed $242.47 of $309.29 total.

Strict gates on a 154-name / 180-day window cannot reliably produce 30 independent
trades. The fix is more point-in-time data across longer windows and broader universes,
not looser gates or more threshold searches on the same sample.

### 3. The news-conditioned mean-reversion hypothesis is untested

Technical mean reversion has been tested and underperformed: -1.32% for Archetype C
and -1.07% for RSI2 V2, where 68 stops cost -$47,133 against +$45,827 of reclaim wins.
What has not been tested is whether fresh, point-in-time news separates transient
liquidity shocks from fundamental repricing.

Chan (2003) studied monthly news/no-news portfolios. It supports testing delayed
reaction and drift, but it does not prove a universal 48-hour veto for this engine's
daily setup. The `reversion-study` path therefore reports three explicit cohorts:
`fresh_news`, `no_identifiable_fresh_news`, and `news_unavailable`. Missing provider
coverage is never classified as no news. Historical studies prefer a trusted
provider-received timestamp and otherwise use a labeled provider-publication-time proxy.

Verdict: **technical-only mean reversion is weak; news conditioning is an unproven
hypothesis requiring an event study before strategy changes**.

### 4. Catalyst drift is data-limited and unproven

The one-shot lifecycle met its structural goal: seven clean trades and zero churn,
where V3 produced hundreds of trades on the same ideas. OOS and walk-forward results
were positive, but seven trades across 14 tickers and about five months of cached
catalysts are insufficient to call the strategy working or broken.

### 5. Promotion is blocked on universe evidence

The Finviz $100B+ universe has no historical membership dates, so
`point_in_time_universe_evidence_missing` fails promotion closed. Any result on that
universe is diagnostic-only regardless of return. This is intentional.

### Solved Causes

State-stack entries, hindsight baskets, missing regime layers, and tiny-sample promotion
are addressed by doctrine and Phase 0. The remaining task is to regenerate evidence
under those controls rather than carrying forward labels from older runs.

## Plan Ordered by Information Gained

### Step 1 - Apply the resolved loss and timing contract

This owner decision is complete:

- planned trade loss is capped from account equity;
- the strategy's structural/ATR stop is preserved;
- position notional has a separate cap;
- swing setups use completed daily bars;
- optional 1h/15m confirmation uses completed bars;
- backtests fill on the next eligible bar;
- stop context ends before the fill bar.

The shared planner and completed-bar execution planner now enforce the same
contract in backtest and paper/live paths.

### Step 2 - Build point-in-time universe evidence

Use archived screen snapshots or another provider that supplies effective-dated
membership and reference data. Cached candles can reconstruct price and liquidity
filters, but not historical Finviz membership, fundamentals, delisting status, or ticker
identity changes. Candle-derived reconstruction alone remains diagnostic-only.

### Step 3 - Run the news/no-news event study before a strategy A/B

First establish whether fresh news changes forward returns for identical oversold
events. The research path supports point-in-time catalyst conditioning:

```powershell
dotnet run --project src/TradingFlow.Cli -- reversion-study `
  --candles-root data/backtest/normalized/440d `
  --news-config configs/backtest/swing-backtest-profile.yaml `
  --catalyst-cache-root data/backtest/normalized/440d `
  --cached-news-only `
  --fresh-news-hours 48
```

Only if event-study cohorts differ materially should the same strategy config be run
veto-on versus veto-off on identical windows and point-in-time universes. Missing news
coverage is an unknown cohort, not evidence of no news. Stop/target changes come later
and must be selected outside the final holdout.

The first cache-only run completed on 2026-07-24:

- 156 tickers had news coverage; 153 had enough bars for analysis, producing 15,380
  evaluated daily observations and 2,581 stretch events.
- 54,376 provider events were available. All used the labeled provider-publication-time
  proxy because the historical cache's receive times describe the later fetch, not the
  original market-time receipt. The report does not pretend otherwise.
- For all stretch events, fresh-news mean returns were 0.29%, 0.65%, 0.84%, and
  0.94% over 1, 2, 3, and 5 sessions. Identifiable-no-fresh-news returns were
  0.24%, 0.52%, 0.53%, and 0.59%.
- For RSI(2)-oversold events above the 200-day trend, fresh-news returns were
  0.57%, 1.09%, 1.31%, and 1.55%; no-fresh-news returns were 0.37%, 0.74%,
  0.74%, and 0.93%.

These are descriptive event returns, not executable portfolio returns or proof of
statistical independence. They reject the proposed *blanket* fresh-news veto on this
sample: fresh news did not identify the weaker cohort. Before any strategy A/B, research
must narrow news by event type, subject mapping, sentiment, novelty, and provider
availability. The loose report was retired after these findings were captured;
future reruns must publish through the evidence catalog.

### Step 4 - Extend catalyst evidence without changing rules

Extend the cached catalyst window and ticker set until there are at least 30 independent
one-shot trades. Then rescore once. Do not tune the strategy while collecting the
missing evidence.

### Step 5 - Cleanly revalidate the strongest hypothesis

Rerun V4 under corrected risk semantics, point-in-time universe evidence, realistic
costs, and untouched holdout data. If it passes, move it to paper shadow observation
with edge-decay tracking before enabling order routing. It is not currently eligible
for production promotion.

### Standing Kill List

- Intraday indicator-stack variants are shelved until a genuinely new universe,
  catalyst, or market-structure hypothesis exists. Do not threshold-search old samples.
- Reversal Reclaim does not generalize off its promoted basket.
- Do not run another threshold search on the existing 154-name / 180-day sample.

## Sequencing Logic

Step 1 applies the resolved risk contract. Step 2 makes historical selection auditable.
Steps 3 and 4 add new information rather than re-mining old samples. Step 5 determines
whether the strongest historical result survives the corrected system.

## Completed-Hourly-Confirmation Offline Diagnostic

The first shared-brain implementation check used only cached Alpaca SIP candles:
10 static symbols, a 180-calendar-day evaluation window, 400 calendar days of
warm-up, daily setup bars, and completed 1h confirmation bars. No provider or
news call was made.

The loose result file was retired after the summary below was captured. Any
reproduction must use the current evidence catalog and immutable artifacts.

| Strategy | Return | Trades | Wins | Max DD | Interpretation |
|---|---:|---:|---:|---:|---|
| Minervini Pivot VCP V7 daily control | +2.4304% | 1 | 1 | 0.0000% | Control wins this tiny diagnostic sample. |
| Minervini Pivot VCP V8 1h confirmation | +0.7475% | 2 | 1 | 0.0735% | Mechanically valid, but not promotion evidence. |

The V8 trades demonstrate the intended sizing behavior: its wider preserved
stops produced smaller quantities while planned account loss remained within
the 1% budget. The run also exposed and fixed two look-ahead/session defects:
fill-bar volume had influenced liquidity/slippage, and exits could route through
premarket bars while extended-hours execution was disabled.

This result is diagnostic only. The universe is a static current list, V8 has
two trades, and one ticker supplied all trades. It does not establish an edge.

The success standard remains: positive on a point-in-time universe, out of sample,
after realistic costs, with at least 30 trades and no single ticker carrying the result.
