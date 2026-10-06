# Backtesting Research Retention

## Run-owned temporary inputs

The operator's 6 October 2026 instruction keeps backtest results and removes
run-owned temporary data after success, failure or cancellation. Published
manifests, metrics, trade/audit outputs and immutable parameters remain available.
The runner and optimizer use a unique `.run-inputs-*` directory for writable
candle/archive inputs and remove it in `finally`. They do not delete read-only CSV
source datasets or another run's input directory. Tests use disposable storage
and must never add lists or screens to the operator source. See the
[implementation and verification receipt](verification/data-isolation-and-run-cleanup-2026-10-06.md).

## Intent

Research evidence is immutable and separate from mutable paper/runtime state.
Preserve published backtest results and immutable parameters, including failed or
uncurated results. Remove owned temporary inputs and abandoned unpublished artifacts.

## Curated Research Package

Each retained run under `data/research/backtests/<run-id>/` may contain:

- `manifest.json`
- `leaderboard.csv`
- `strategy_metrics.csv`
- `ticker_metrics.csv`
- `trades.csv`
- `diagnostics.json`
- resolved run and strategy snapshots
- concise notes and promotion decision

Do not retain run-owned temporary provider payload copies, worker logs or abandoned
unpublished generated configs. Published exploratory results and their immutable
parameters survive even when the run failed. Disposable test outputs are removed
after tests. Read-only source candle datasets and operational caches remain separate
and are not deletion targets of a backtest's workspace cleanup.

## Strategy Lifecycle

Canonical and backtest strategy files remain research artifacts until an explicit
authorization binds strategy ID, semantic version, content hash, dataset evidence,
and a human decision. Folder location and a run-local winner label are not promotion.

The current catalog has two research swing artifacts and thirteen archived swing
artifacts. The integrated decision is `RETAIN_RESEARCH`; none is authorized for a new
paper-shadow or live run.

## Runtime State

Paper/live warm state belongs under the mode-specific runtime data root and never
depends on a research result folder. Daily and declared sub-daily swing state is
restored from historical evidence, updated from completed bars, and persisted through
atomic writers.

## Retention Modes

- `summary`: manifest, portfolio metrics, diagnostics aggregates, and strategy identity.
- `full`: summary plus trade, candidate, gate, and execution-event detail.

The shared Web run workflow sends and requires `full` so its published result
includes trade and workflow evidence. `summary` remains a separately declared
retention mode where supported. A run cannot silently change retention after start.

## Observability

Jobs emit structured lifecycle events for queued, started, progress, completed,
failed, and canceled states. Metrics include duration, provider failures, cache use,
candidate counts, accepted/rejected decisions, trades, and artifact size.
