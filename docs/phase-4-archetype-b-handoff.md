# Phase 4 (Archetype B — Catalyst One-Shot Lifecycle) — Handoff / Morning Note

Resume point for building Archetype B (post-catalyst drift / episodic pivot). Context: doctrine §6B +
`docs/edge-recovery-master-plan.md` Phase 1. Everything through Phase 3 and this core is committed and
pushed to `origin/codex/architecture-optimization`.

## Done in this session (committed)
`CatalystEligibilityService` (`src/TradingFlow.Engine/Catalysts/`) — the lifecycle CORE, pure and fully
unit-tested (6 tests in `CatalystEligibilityServiceTests`):
- **KeyFor** — dedupe by `ExternalId` else normalized headline, ticker-scoped (same story from two
  providers → one catalyst).
- **Eligibility** — received-time clock (`CatalystEvent.ReceivedAt`); falls back to published `Timestamp`
  with `UsedPublishedFallback = true` (audit/research-only flag).
- **ResolveWindow** — first candle STRICTLY after eligibility (excludes the still-forming bar = no
  lookahead), bounded to N candles.
- **TryBeginAttempt / IsConsumed** — one attempt per catalyst, ever (the structural anti-churn invariant).

## Remaining steps (in order)
1. **Wire the lifecycle into the attach path.** `CatalystSnapshotAttacher` currently attaches the latest
   catalyst to EVERY snapshot (the V3 anti-pattern). Change it so a catalyst is offered to the evaluator
   only within its confirmation window and only until consumed. Service is per-run (backtest) / persisted
   (paper-live).
2. **Add `catalyst_drift` (V5) setup type** to the shared `BasicStrategyEvaluator`: within the confirmable
   window, require the validated event-study bucket (positive/new general news OR earnings/guidance) +
   technical confirmation. Evaluate ONCE; on evaluation call `TryBeginAttempt` to consume.
   - **Building block DONE:** `CatalystConfirmation.HasTechnicalConfirmation(current, previous)` (pure,
     5 tests) — EMA10×20 flip OR MACD-histogram turn-positive + volume expansion. Compose it with
     `CatalystEligibilityService.ResolveWindow` (window) + `TryBeginAttempt` (one-shot) in the runner.
   - Still to do: the bucket/news gate reuse + wiring the two building blocks into the attach path/runner
     so the setup fires once per catalyst.
3. ~~**Remove the dead `catalyst_confirmation_swing` handler**~~ **DONE** — `IsCatalystConfirmationSwing` +
   its switch case in `BasicStrategyEvaluator` and its 4 `GetLongEntryRejection_WhenCatalyst*` tests are
   removed (the re-tradable-every-bar anti-pattern the lifecycle replaces). 239 tests green.
4. **Persistence** for paper/live: a SQLite-backed consumed-set so a restart never re-fires a story
   (backtest stays in-memory).
5. **V5 config** `configs/strategies/swing-catalyst-drift.v5.yaml`: Screen B pool; exits = trend-rider
   style (SMA10/20 cross, ATR trail after 1R, 3–5 day hold, `allow_same_bar_stop_target: false`, NO VWAP
   exits — wrong horizon).
6. **Honest rescore** + `promotion-check`, scored across the event-study cohorts + ONE reserved holdout
   window (score the holdout exactly once). Acceptance: V3-style churn structurally impossible; backtest
   and live share the same eligibility path; positive on the holdout.

## Data / feasibility (CONFIRMED offline)
Cached catalysts exist: `data/backtest/normalized/{150d,240d}/{ticker}/catalysts_alpaca_*.json`, read by
`CachedCatalystProvider`. **Use the 240d cache** — it has both `bars_1d.csv` AND catalyst JSON for ACHR,
APLD, and others. So V5 is fully backtestable offline; no news-archive sub-project needed.

## Key correctness (do not rush)
- `ReceivedAt` is the clock, not published `Timestamp`.
- First confirmable = first candle STRICTLY after eligibility (never the forming bar).
- One attempt per `CatalystKey`, ever.
- Dedupe so one story = one catalyst.

## Landing points
- `src/TradingFlow.Backtesting/CatalystSnapshotAttacher.cs` — attach path to change
- `src/TradingFlow.Engine/Strategies/BasicStrategyEvaluator.cs` — V5 setup type + remove the dead handler
- `src/TradingFlow.Engine/Strategies/SignalGenerator.cs` — catalyst signal context
- `src/TradingFlow.Data/Catalysts/CachedCatalystProvider.cs` — offline catalysts
- `src/TradingFlow.Backtesting/BacktestRunner*.cs`, `LiveRunner.cs` — per-run service + gating

## Update 2026-07-11 — backtest lifecycle WORKING
Steps 1–2 done for the **backtest path**:
- `catalyst_drift` setup type + `SignalGenerator.IsCatalystDriftTrigger` compose `CatalystConfirmation`
  (EMA10×20 flip / MACD turn + volume) with one-shot-per-catalyst-run (first confirmed bar of the run).
- Fixed `CachedCatalystProvider` to reuse any cached fetch whose window **covers** the request (was
  exact-match only → 0 catalysts offline). This unblocked offline catalyst backtesting.
- Re-score (`archetype-b-rescore-240d.yaml`): **7 clean one-shot trades, +0.02%, REJECTED on sample size**
  — but ZERO churn (the structural goal). 244 tests green.

Remaining:
- **LiveRunner one-shot wiring** — the live path still attaches catalysts to every bar; wire the eligibility
  service (`ResolveWindow` + `TryBeginAttempt`) + `CatalystConfirmation` into `LiveRunner` for real-time
  one-shot, and **SQLite persistence** (step 4) so restarts don't re-fire. The static `ResolveWindow` +
  `HasTechnicalConfirmation` are the shared building blocks to compose there.
- **More data for a promotable sample** — longer window / more tickers with cached catalysts → more trades.
- Optional: a dedicated `SignalGenerator` catalyst_drift unit test (currently proven by building-block
  unit tests + the 7-trade e2e re-score).

## State
244 tests green. Also open: paper-validate V4 (task, operational) and the lossy-`WriteStrategyYaml`
one-brain fix (spawned task `task_93ae7caa`).
