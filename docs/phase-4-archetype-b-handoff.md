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
   technical confirmation (EMA10×20 flip OR MACD turn + volume expansion). Evaluate ONCE; on evaluation
   call `TryBeginAttempt` to consume.
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

## State
243 tests green. Also open: paper-validate V4 (task, operational) and the lossy-`WriteStrategyYaml`
one-brain fix (spawned task `task_93ae7caa`).
