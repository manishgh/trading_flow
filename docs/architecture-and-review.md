# TradingFlow — Architecture, Design, and Senior Code Review

Living document. Part 1 explains what the system is and how it's built. Part 2 is a
senior review: what is well-designed, what is over-engineered, and a *safe*
modularization plan for the large files — done as verified increments so no function
is ever silently lost.

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
| B | `BacktestRunner` simulation | Unify long/short exit engine behind a `direction` parameter; delete the duplicate copy only after a golden-output test confirms identical trades. | Medium — gated by a characterization test first |
| C | `MobileAutomationService` | Extract `AutomationExitMonitor` (the guardian loop) and `AutomationSessionStore` usage into their own classes. | Low–Med |
| D | `SignalGenerator` / evaluator | Group entry gates into `TrendGates`, `VolumeGates`, `NewsGates`, `StructureGates` static checkers. | Medium |
| E | Mobile | Split `TradingFlowApiClient` DTOs into a `Contracts` file; split `WishlistsPage` view-models out. | Low |

Recommended order: **A → C → E** (all low-risk, immediate line-count win), then a
characterization test before **B**, then **D**. Do not attempt B without the golden test.

### 2.5 Guardrails for this refactor (the #7 promise)
- No behavior change in any "cleanup" commit; behavior changes are separate, reviewed commits.
- Full test suite green after every increment (currently 222 tests).
- For the long/short unification (B), write a characterization test that captures current
  trades for a fixed fixture *before* touching the code, and require byte-identical output.
- Keep public method signatures stable; add optional params rather than reshaping call sites.
