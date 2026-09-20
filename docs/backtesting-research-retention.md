# Backtesting Research Retention

## Intent

Research evidence is immutable and separate from mutable paper/runtime state. Keep
only artifacts needed to reproduce a swing result or explain a promotion decision.

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

Do not retain provider payload copies, temporary worker logs, abandoned generated
configs, or failed exploratory output once its reusable lesson is encoded in tests or
the research status. Candle caches remain separate and are not deleted as part of
strategy cleanup.

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

UI and routine worker runs default to `summary`. Promotion, defect reproduction, and
explicit audit runs use `full`. A run cannot silently change retention after start.

## Observability

Jobs emit structured lifecycle events for queued, started, progress, completed,
failed, and canceled states. Metrics include duration, provider failures, cache use,
candidate counts, accepted/rejected decisions, trades, and artifact size.
