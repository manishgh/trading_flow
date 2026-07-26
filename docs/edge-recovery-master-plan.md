# Edge Recovery Master Plan (2026-07)

Deep review of `docs/architecture.md`, `docs/strategy-last-runs.md`,
`docs/strategy-forensics-2026-06-13.md`, `docs/backtesting-bias-controls.md`,
`docs/candle-news-pipeline-audit.md`, the evidence-catalog research design,
`docs/backtesting-research-retention.md`, the strategy YAML catalog, and the
catalyst gate code in `BasicStrategyEvaluator`. Written as a hand-off plan:
each phase is independently executable and states its acceptance criteria.

## The One-Paragraph Diagnosis

The engine, pipeline, and indicator math are largely sound — the forensics
docs verify RVOL semantics, one-brain evaluation, and bias-safe bar handling.
What is missing is not another indicator or another v11 gate tweak. The system
has four structural holes: (1) every backtest runs on hand-picked baskets of
stocks already known to have moved, so measured "edge" is contaminated by
hindsight selection and evaporates on any honest universe; (2) the catalyst
half of the system has research proving signal (the event study) but no
per-catalyst execution lifecycle, so every catalyst rule either fires zero
times or churns the same story bar after bar; (3) there is no market/regime
context anywhere in the strategy schema, so swing results are indistinguishable
from "semis went up that quarter"; (4) promotion decisions are made from tiny,
single-window samples with no enforced out-of-sample or walk-forward gate.
Fix those four and the existing strategies can be judged honestly; skip them
and every future strategy version will repeat the endless-version treadmill.

## Evidence Base (do not re-litigate)

| Fact | Source |
|---|---|
| Retained intraday TOP1 is ~flat: 0.09% / 7d, 35% WR | strategy-last-runs.md |
| All strongly positive runs used pre-selected movers (POET/RGTI/MXL "volatile five", "known runners", SNDK/MU/WDC/STX) | strategy-last-runs.md, retention doc |
| On non-preselected/volatile names every tested intraday variant is negative | strategy-last-runs.md |
| Catalyst event study found real signal: positive/new news + technical confirmation → favorable 1d–5d forward returns (384 + 600 observations) | 2026-07-05 event study section |
| Every catalyst → trade-rule translation failed: V2 0 trades, V3 −4.18%/−17.49% (churn), V4 −0.92%/−1.34% | strategy-last-runs.md |
| Docs' own diagnosis: need one trade attempt per catalyst per ticker with a bounded confirmation window; today a catalyst stays attached to every snapshot in the lookback window | strategy-last-runs.md conclusion + code audit note |
| Live eligibility must use `receivedTimestampUtc`, not provider-published time; designed, not enforced in the trading path | Catalyst Published Timeline Audit |
| Losses come from exit design, not signal detection: `technical_exit_below_vwap` fired 853× on one model; no runner state machine; partial exits unsupported (`BacktestTrade` = one entry, one exit) | strategy-forensics-2026-06-13.md, architecture.md |
| Swing winners need market/sector context (MSFT/MU losses); no regime filter exists in schema | strategy-forensics-2026-06-13.md |
| Bias doc's open items: point-in-time universe provider, corporate-action-adjusted data, liquidity/partial-fill modeling | backtesting-bias-controls.md |
| Promotion criteria documented but not enforced (short strategy promoted on 2 trades; samples of 10–44) | backtesting-research-retention.md |
| 2026-07-05 horizon finding: the target names move on weekly horizon; same-session intraday systems structurally cannot capture it; a catalyst-aware multi-day swing model is the proposed-but-unbuilt answer | strategy-last-runs.md |

## Root Causes, Ranked by Expected Impact

1. **Hindsight universe selection.** Static ticker YAML of known movers means
   the backtest answers "could I ride POET after knowing POET ran", never
   "would my scanner have surfaced POET that morning". This single flaw can
   fabricate the entire measured edge.
2. **Catalyst lifecycle missing from the trading path.** The event study
   (research) dedupes stories, scores novelty, anchors to point-in-time
   candles, and measures a bounded confirmation window. The trading path
   (`CatalystSnapshotAttacher` → `SignalGenerator` → `BasicStrategyEvaluator`)
   just attaches "latest catalyst within lookback" to every snapshot — so
   gates like `max_catalyst_confirmation_bars` (evaluator ~line 749) only
   trim age; they cannot express "one attempt per story, evaluated once".
3. **No regime/market context.** Nothing in `StrategyDefinition` can express
   "only long when SPY > 50dma" or "sector ETF above its 20dma". Swing
   results ride sector beta invisibly.
4. **Exit/trade management mismatch.** Reactive single-condition exits shake
   out runners; no partial exits; no per-ticker session loss guard (the
   documented "next design step" — still unbuilt).
5. **No enforced validation protocol.** No mandatory OOS window, walk-forward,
   minimum sample, or benchmark delta before a strategy enters the promoted
   catalog. The endless-version churn on the same baskets is an overfitting treadmill.
6. **Cost realism.** bps slippage only; low-float runners (POET, RGTI) would
   see spread blowouts and partial fills that bps cannot represent.

## Plan

### Phase 0 — Make results trustworthy (prerequisite for everything)

**0.1 Point-in-time universe provider.**
- New `IUniverseProvider` with a `HistoricalScreenerUniverseProvider`: for each
  backtest day, select tickers from cached daily data using only prior-day
  information (min price, min dollar volume, gap %, prior-day RVOL, float
  proxy if available). Persist daily universe snapshots under
  `data/research/universe/{date}.json` for reproducibility.
- Run config gains `universe.mode: static | historical_screener` with the
  screener rule inline. `BacktestRunner` resolves tickers per-day (or per-run
  window as a first increment) via the provider instead of the static list.
- Re-run the four retained strategies on an honest universe; record the new
  baselines in `docs/strategy-last-runs.md`. Expect them to get worse —
  that is the point. This is the new floor to improve from.

**0.2 Enforced promotion gate.**
- Add `PromotionCriteria` evaluation into `BacktestResult` post-processing:
  min accepted trades (e.g., ≥30), max single-ticker profit share (e.g., ≤40%),
  OOS partition configured and positive, benchmark (buy-and-hold same
  universe) delta reported, walk-forward windows ≥2. Emit a
  `promotion_eligible` flag + reasons in diagnostics; the web/CLI leaderboard
  shows it. Promotion without the flag requires an explicit override note in
  the ledger.

**0.3 Cost realism for thin names.**
- Extend the fill model: max participation as % of bar volume (reject or
  partially fill beyond it), and spread-proxy slippage (scale bps by
  ATR%/price or a per-ticker liquidity tier). Keep the current model as
  default; strategies opt in via run config so old results stay comparable.

*Acceptance: retained strategies re-scored on historical-screener universes,
ledger updated, promotion flag visible in leaderboard, tests for the universe
provider's no-lookahead property (a ticker enters the universe only using
data available before the session).*

### Phase 1 — Catalyst trading brain (the missing half of "technical + catalyst")

**1.1 Catalyst lifecycle store.**
- `CatalystKey = (ticker, story dedupe hash)` reusing the event-study dedupe
  logic from `CatalystTechnicalEventStudyRunner`. States:
  `detected → confirmable(window) → attempted | expired`. Persist per-run in
  memory for backtest; in SQLite for paper/live (survives restarts).
- **One trade attempt per catalyst per ticker.** After an attempt (accepted or
  rejected at confirmation time), the catalyst is consumed.

**1.2 Shared eligibility service.**
- `CatalystEligibilityService` used by both `BacktestRunner` and `LiveRunner`:
  eligibility clock = `receivedTimestampUtc` (fall back: treat catalyst as
  research-only and emit audit flag, per the timeline-audit design note).
  Confirmation may begin only at `firstConfirmableTimestamp` (first candle
  strictly after received time) — never the still-forming candle.

**1.3 Event-study bucket → rule translation (Catalyst Swing V5).**
- Entry evaluated once, at the end of the bounded confirmation window
  (e.g., first 2–6 bars of 15m/1h after first-confirmable): require the
  bucket conditions the study validated — positive/new general news or
  earnings/guidance, plus technical confirmation
  (`ema10CrossedAboveEma20AfterNews` or MACD histogram turn + volume
  expansion). Category and novelty gates from the study fields.
- Exits: reuse the trend-rider structure (SMA10/20 cross, ATR trail after 1R,
  3–5 day max hold, `allow_same_bar_stop_target: false`). No VWAP exits —
  wrong horizon.
- Backtest via Phase 0 universes across both cohorts used in the study, plus
  one untouched holdout window reserved before any tuning starts.

*Acceptance: V3-style churn is structurally impossible (a story can produce at
most one attempt); backtest/paper share the same eligibility code path; V5
result recorded against the study's own cohorts + holdout.*

### Phase 2 — Regime context and the volatile-swing archetype

**2.1 Regime gate in the strategy schema.**
- New `regime` block: benchmark symbol (SPY/QQQ or sector ETF), rule
  (`price_above_sma50`, `sma20_above_sma50`, or `off`), evaluated from daily
  candles of the benchmark fetched like any other ticker. Applied as an entry
  gate and optionally as a "flatten new entries" filter in paper/live.

**2.2 Volatile swing model (per the 2026-07-05 finding).**
- 1d trend/mean-reversion context + 15m/1h entry timing + multi-day hold +
  catalyst awareness (Phase 1 service). This is the fusion strategy the
  system's stated goal actually implies; most past effort went into intraday
  scalping variants that the July finding shows are horizon-mismatched.

**2.3 Per-ticker session guard (documented "next design step").**
- Portfolio-level: pause a ticker after configurable realized losses or N
  failed signals in a session/window. Config portable between backtest and
  paper (`portfolio.ticker_guard`).

*Acceptance: regime gate has unit tests + appears in diagnostics as rejection
reason; volatile-swing candidate scored on honest universe with promotion
gate; ticker guard demonstrably caps repeated-loss churn in a replay test.*

### Phase 3 — Exit and trade management (only after 0–2)

- Trade legs model: `BacktestTrade` → entry + multiple exit legs with per-leg
  realized P/L, enabling partial exits (architecture.md names this the
  blocker). Migrate result JSON with a version field.
- Runner state machine for intraday (opening drive / first pullback / reclaim
  / continuation / exhaustion) — only if intraday remains a goal after honest
  re-scoring; otherwise deprioritize intraday entirely.
- Confirmed structural exits everywhere: N-bar confirmed VWAP/EMA breaks
  (fields already exist: `enable_confirmed_vwap_exit`) instead of raw touches.

### Phase 4 — Validation infrastructure and edge tracking

- Walk-forward automation in `BacktestRunner` (train/validate window rolling),
  writing per-window metrics into diagnostics; ledger records the spread, not
  just the full-window number.
- Experiment manifest: every research run declares its tuning windows and its
  reserved holdout; the runner refuses to score the holdout more than once
  per strategy version.
- Edge-decay tracking: nightly job compares paper results per strategy vs its
  backtest expectancy band; alert (existing notifications feed) when live
  performance departs from the band.
- Bootstrap confidence interval on expectancy per strategy in the analyzer
  output, surfaced in the leaderboard next to raw return.

## Explicit Non-Goals

- No new indicators, and no v11 gate tweak on the existing baskets — forensics
  already concluded "do not add more indicators first".
- Statistical analysis remains a research assistant; it does not become a
  trade executor in this plan.
- No live-money enablement; the live router stays disabled by default.

## Code Landing Points for Opus

| Area | Files |
|---|---|
| Catalyst gates (trading path) | `src/TradingFlow.Engine/Strategies/BasicStrategyEvaluator.cs` (~lines 720–820), `src/TradingFlow.Backtesting/CatalystSnapshotAttacher.cs`, `src/TradingFlow.Engine/Strategies/SignalGenerator.cs` |
| Catalyst research (reuse dedupe/novelty/anchoring) | `CatalystTechnicalEventStudyRunner` (CLI), `src/TradingFlow.Engine/Abstractions/ICatalystProvider.cs`, `src/TradingFlow.Data/Catalysts/CatalystStreamer.cs` |
| Runners | `src/TradingFlow.Backtesting/BacktestRunner.cs`, `src/TradingFlow.Backtesting/LiveRunner.cs` |
| Strategy schema / YAML | `src/TradingFlow.Domain/Strategies/StrategyDefinition.cs`, `src/TradingFlow.Engine/Configuration/SimpleYamlReader.cs` (+ `RunConfigParsingTests`) |
| Portfolio / risk | `src/TradingFlow.Engine/Risk/RiskEngine.cs`, portfolio settings in run config |
| Trade model (partial exits) | `TradingFlow.Domain.Backtesting.BacktestTrade` and result projections |
| Universe today | static `tickers:` lists in `configs/backtest/*.yaml`, Finviz live screener in `src/TradingFlow.Finviz/FinvizClient.cs` |
| Ledger to keep updated | `docs/strategy-last-runs.md` |

## Suggested Order of Execution

Phase 0.1 → 0.2 (one PR each) → re-score baselines → Phase 1 (single PR:
lifecycle + eligibility + V5 config) → Phase 2.1/2.3 (small PRs) → 2.2 →
re-evaluate whether intraday deserves Phase 3 investment → Phase 4 alongside.

The success metric for this plan is not "a backtest shows +X%". It is: a
strategy that is positive on a point-in-time universe, out-of-sample, after
realistic costs, with ≥30 trades and no single ticker carrying it — because
that is the only kind of result that has ever had a chance of surviving paper
and live.
