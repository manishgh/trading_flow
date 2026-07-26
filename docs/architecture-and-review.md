# TradingFlow — Architecture, Design, and Senior Code Review

Living document. Part 1 explains what the system is and how it's built. Part 2 is a
senior review: what is well-designed, what is over-engineered, and a *safe*
modularization plan for the large files — done as verified increments so no function
is ever silently lost.

System scope, provider constraints, the 2016 evidence floor, later-listing
eligibility, point-in-time timing, execution realism, and promotion limits are
binding in [TradingFlow Operating Boundaries](operating-boundaries.md). This review
may recommend structural changes but must not weaken those boundaries.

---

## Part 1 — Design and Purpose

### What it is
A research → paper → (gated) live trading system for US equities that trades on
**technical + catalyst** signals. Core promise: **one brain**. The same strategy
evaluation path runs in backtest, paper, and live; only the data *source* and the
execution *sink* change.

```
OHLCV candles
  -> required-timeframe derivation
  -> indicator snapshots  (+ catalyst/news attach)
  -> confluence gate
  -> strategy signal
  -> entry validation
  -> portfolio / risk sizing
  -> execution sink: backtest sim | paper broker | live broker
```

### Project map (responsibility per assembly)
| Project | Responsibility |
|---|---|
| `TradingFlow.Domain` | Pure records/enums: strategies, run config, backtest results, orders, wishlists, news. No I/O. |
| `TradingFlow.Engine` | The brain: indicators, signal generation, evaluator, risk, candle pipeline, universe screen, sessions, config parsing. |
| `TradingFlow.Backtesting` | Batch simulator (`BacktestRunner`) and `LiveRunner` (paper/live loop) built on the Engine. |
| `TradingFlow.Data` | Persistence: CSV candles, SQLite (jobs, orders, wishlists, news), catalyst streamer, rolling warm-up cache. |
| `TradingFlow.Alpaca` / `TradingFlow.Finviz` | Broker + market-data + news + screener providers behind Engine interfaces. |
| `TradingFlow.Web` | ASP.NET Razor pages + `/api/mobile` for the app + SSE streams. Resolves config/credentials and calls Engine APIs; does **not** compute indicators. |
| `TradingFlow.WarmupService` | Standalone service that maintains the rolling warm-up candle cache. Binds `:53120`. |
| `TradingFlow.Mobile` | .NET MAUI Android client. |
| `TradingFlow.Cli` | Command-line entry for runs/research. |

### Data & mode isolation
Backtest / paper / live write to separate roots (`data/{mode}/...`). Backtest data
is disposable; paper/live is operational state. Warm-up cache is separate from research
snapshots. This separation is a genuine strength — keep it.

### Trustworthiness layer (recent work, phase 0)
- **Point-in-time universe** (`HistoricalScreenerUniverseProvider`): no-lookahead
  selection, per-run or per-day, static / Finviz / both candidate pools.
- **Execution realism** (`ExecutionRealismModel`): bar-participation fill cap + SEC/FINRA
  regulatory costs.
- **Promotion gate** (`PromotionEvaluator`): min trades, OOS, benchmark, walk-forward,
  ticker-concentration.
- **Universe evidence gate** (`UniversePromotionEligibilityValidator`): promotion fails
  closed unless the run carries a point-in-time membership ledger and provider snapshot
  evidence. A current Finviz export remains diagnostic-only.
- **Shared order planning** (`SharedOrderRiskPlanner` + `StrategyOrderPlanner`): backtest,
  paper, and notification automation use the same side-neutral sizing, structural-stop,
  fee, account-risk, and position-notional rules.
- **Portfolio admission audit**: full-retention results distinguish signal rejection from
  universe/regime rejection, slot contention, ticker overlap, liquidity, and order-plan
  rejection. A no-trade result is therefore attributable instead of opaque.
- **Unified portfolio simulation**: multiple strategies compete chronologically for one
  capital pool and one set of position slots. Candidate ranking is explicit in strategy
  config; ties are deterministic.
These are already small, single-purpose, tested classes — the target shape for the rest.

---

## Part 2 — Senior Review

### 2.1 What is designed well (keep as-is)
- **One-brain evaluation** and **mode/data isolation** — the core architecture is sound.
- **Candle pipeline** (`CandlePipelineEngine`): bounded TPL-Dataflow, keyed parallelism,
  ordered-within-key, backpressure, `ICandleStore` persistence boundary. This is the
  right shape. The documented gap (batch derivation vs a rolling streaming aggregator)
  is a real future item but not a defect for current batch/paper cadence.
- **Catalyst path**: `ICatalystProvider` → `CatalystSnapshotAttacher` attaches the latest
  *fresh* catalyst before signal generation; the evaluator applies news gates; paper/live
  keeps a final negative-news veto. The design is correct. The one substantive gap
  (already flagged in the edge-recovery plan) is a **per-catalyst lifecycle** so a story
  produces one bounded trade attempt instead of re-firing every bar — that's a feature,
  not a cleanup.
- The new phase-0 modules are already clean and single-responsibility.

### 2.2 Over-engineering / friction to trim (keep behavior, reduce surface)
1. **`BacktestRunner.cs` is 2,946 lines** doing far too much: window resolution, provider
   creation, universe, market prep, per-ticker simulation (long + short), portfolio
   assembly, diagnostics, result writing, benchmark. This is the #1 modularity problem.
2. **Long/short simulation is duplicated** (`CreateCandidate` long path and
   `CreateShortCandidate`) with ~8 near-identical exit branches each and 15 `BuildCandidate`
   call sites. Direction should be a parameter to one exit engine, not two parallel copies.
3. **`SignalGenerator.cs` (1,774) and `BasicStrategyEvaluator.cs` (951)** carry a very large
   flat surface of boolean entry flags. Much is essential expressiveness, but the gate
   evaluation could be grouped (trend gates / volume gates / news gates / structure gates)
   into small cohesive checkers instead of one long method.
4. **`MobileAutomationService.cs` (1,109)** mixes session store, entry sizing, the exit
   monitor loop, and market-state prep. The exit monitor is its own concern.
5. **`TradingFlowApiClient.cs` (1,018)** and **`WishlistsPage.xaml.cs` (1,092)** on mobile
   mix DTOs + transport + (for the page) several view-models in one file.
6. **`StrategyDefinition` entry-rule flag count** is large; not wrong, but a config the
   evaluator groups would read better. Low priority.

Nothing above is *wrong logic* — it's surface area. The risk is all in the refactor, so:

### 2.3 Safe modularization method (how, not just what)
Use **partial classes and pure extracted helpers**, never a rewrite:
- **Partial-class split**: move cohesive method groups of a big class into
  `ClassName.Area.cs` files (`partial class`). Same type, same private members, **zero
  behavior change**; the compiler proves completeness and the full test suite proves
  behavior. This is the safest possible reduction of a 1,000+ line file.
- **Pure helper extraction**: lift stateless computation (e.g., fill/cost, sizing,
  diagnostics assembly) into small static classes in the Engine — testable in isolation,
  reused by backtest and paper (reinforces one-brain).
- **Every increment**: extract → `dotnet build` → **full `dotnet test`** → only then next.
  A green suite after each step is the guarantee against dropped functions (#7).

### 2.4 Proposed increments (each independently verifiable)
**Status log**
- **A — DONE (2026-07-06).** `BacktestRunner` split into partial files with zero logic
  change, full suite green (222) after each step:
  `BacktestRunner.cs` 2,946 → 1,297 lines, plus `.Universe.cs` (81), `.Simulation.cs`
  (774, the long/short exit engine), `.Providers.cs` (180), `.Diagnostics.cs` (682).
  The remaining Simulation size is the duplicated long/short engine — that's increment B,
  intentionally not touched without a characterization test.

| # | Target | Action | Risk |
|---|---|---|---|
| A | `BacktestRunner` | ✅ Split into partials (`.Universe`, `.Simulation`, `.Providers`, `.Diagnostics`). Verified. | Done |
| B | `BacktestRunner` simulation | **Reassessed — do NOT fully merge (see ADR below).** Characterization guardrail added instead. | Done (decision) |
| C | `MobileAutomationService` | ✅ Split into partials: `.ExitMonitor` (guardian loop) and `.EntryPreparation` (market-state + entry gates). 1109 → 476 main. Verified. | Done |
| D | `SignalGenerator` / evaluator | Group entry gates into `TrendGates`, `VolumeGates`, `NewsGates`, `StructureGates` static checkers. | Medium |
| E | Mobile | Split `TradingFlowApiClient` DTOs into a `Contracts` file; split `WishlistsPage` view-models out. | Low |

Recommended order: **A → C → E** (all low-risk, immediate line-count win), then a
characterization test before **B**, then **D**. Do not attempt B without the golden test.

### 2.4a ADR — Increment B: keep long/short exit loops as mirrors (do not merge)

**Decision (2026-07-06):** After reading both `CreateCandidate` (long) and
`CreateShortCandidate` (short) in full, we will **not** collapse them into one
direction-parameterized method.

**Context.** The two loops are mirror images: stop below vs above entry, target above
vs below, `price <= stop` vs `price >= stop`, `confirmed_vwap_failure` vs
`confirmed_vwap_reclaim`, and per-direction slippage. A merged method would carry a
`direction == "long" ? … : …` branch at ~15 sign-sensitive comparison points.

**Why not merge.**
1. This is money-critical logic; a single inverted comparison is a real P&L loss, not a
   cosmetic bug. Two mirror methods are auditable at a glance; a branchy merged method is not.
2. The genuinely reusable, direction-agnostic pieces are **already** extracted and take a
   `direction` parameter: `ResolveInitialRisk`, `ResolveTakeProfitPrice`,
   `ShouldExitFailedBreakout`, `BuildCandidate`, plus per-direction slippage helpers. The
   mechanical duplication is already gone; only the intentional mirror loop remains.
3. DRY is not an absolute. For correctness-critical mirror logic, explicit beats clever.

**What we did instead.** Added `BacktestSimulationCharacterizationTests` freezing the
sign-sensitive directional risk core (long and short stop/target are exact mirrors around
entry). This permanently guards the two implementations against silently diverging and would
make any future merge provably behavior-preserving — so the option stays open, safely.

### 2.5 Guardrails for this refactor (the #7 promise)
- No behavior change in any "cleanup" commit; behavior changes are separate, reviewed commits.
- Full test suite green after every increment (currently 222 tests).
- For the long/short unification (B), write a characterization test that captures current
  trades for a fixed fixture *before* touching the code, and require byte-identical output.
- Keep public method signatures stable; add optional params rather than reshaping call sites.
### Repository-relative run paths

Market-data and result roots are repository-relative. Repository discovery uses
`TradingFlow.sln` as the marker; a nested research directory named `configs` is not a
repository root. This prevents research bundles from silently creating duplicate candle
caches or writing results to an unintended nested tree.

### Resolved stop provenance in portfolio admission

Candidate generation resolves the strategy stop before portfolio admission.
The candidate persists whether that resolved price came from an ATR rule or a
structural rule. Unified portfolio admission must pass both the resolved price
and its stop kind to `SharedOrderRiskPlanner`; reconstructing every price as a
structural stop corrupts audit codes even when the numeric decision is unchanged.

The planner never narrows either stop to force admission. It preserves the
resolved invalidation price and sizes quantity from the account-equity risk
budget, subject to the separate maximum position-notional cap.

### Daily swing setup versus intraday execution

A daily structural or multi-ATR stop is commonly wider than 1% of entry price.
That is valid when quantity is reduced so the planned dollar loss remains within
the account-equity risk budget. Position notional is constrained independently.

The implemented shared execution contract:

1. form the setup from a completed daily bar,
2. confirm on a completed 1h or 15m bar,
3. fill no earlier than the next executable bar,
4. derive the initial stop only from data known before that fill,
5. retain daily technical management while continuously honoring the hard
   broker stop, and
6. use the same planner and state machine in backtest, paper, and live modes.
