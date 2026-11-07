# Backtesting Research Retention

## Run-owned temporary inputs

The operator's 21 October 2026 clarification protects original market data, news,
earnings, provider receipts, wishlists and saved Finviz lists. Broad cleanup requests
never authorize deletion of core data. Challenge even a user's destructive request,
explain the exact datasets and consequences, and obtain fresh dataset-specific
confirmation. See [AGENTS.md](../AGENTS.md#core-data-deletion-requires-specific-approval).

Original provider responses belong under the accepted run's existing `RawRoot`,
outside the unique `.run-inputs-*` folder. Built-in online providers archive response
bytes and manifests before parsing. Nested preparation/optimizer runs reuse this
durable archive owner. Automatic archive expiry is disabled, including dates older
than the minimum preservation period.

Validated reusable provider candles and exchange calendars live under the original
`NormalizedRoot/_historical/<exact-request-hash>/`. Their version manifests, original
response receipts and every committed version remain protected core data. Refresh
publishes a new version; expiry changes selection for a new comparison and never
authorizes deleting an old version. Each accepted comparison retains its immutable
data request and selected-version manifest beside its retained run parameters.
Rejected admissions remove only their uniquely owned unpublished request files after
checking durable job ownership. Local CSV diagnostics retain their existing
file-declared source without a verified provider-history snapshot claim.
See [prepared-candle design](design-review/2026-10-19/08-prepared-candle-reuse-2026-10-21.md).

The runner and optimizer release prepared-data handles in `finally` after success,
failure or cancellation, but retain generated projections, candle spills and
execution journals in their private `.run-inputs-*` folder until an independent
quant reviewer audits that run's simulated trades/accounting or failed attempt and
explicitly approves its cleanup. The saved approval binds the exact terminal
workspace-manifest digest and published result hash when present. Use the shared
`backtest-cleanup-reviewed-inputs --review <saved-quant-review.json>` CLI operation.
Its schema-2 approval inventories every file as disposable or protected, with exact
relative path, SHA256 and byte length. It checks ownership, terminal state, result
bytes, resolved paths and the complete inventory before deleting only approved
generated files from `candles`, `.execution-journal` and `.candidate-journal`.
Normalized originals remain in place, even inside an old run-input folder. Unknown,
changed or missing files refuse cleanup; schema-1 reviews cannot authorize deletion.
Keep the workspace manifest and approval outside the spool. Stop all workspace
writers first. A late change or cancellation can leave partial removal of approved
copies; record that outcome and obtain a fresh inventory review before resuming.
This approval permits intermediate cleanup only, not trading or strategy promotion.
Original responses, outside read-only CSV/frozen sources, published manifests,
monthly/trade/audit results and immutable parameters remain. Tests use isolated
synthetic storage, remove their disposable fixtures after tests stop, and must
never add lists or screens to the operator source. See the earlier
[implementation and verification receipt](verification/data-isolation-and-run-cleanup-2026-10-06.md).

New normalized catalyst assessments are archived as exact UTF8 payloads under the
existing `RawRoot` before a cache copy is published. The cache proof must reference
that separate durable archive, with matching original writer identity, manifest and
payload bytes. Same-path filesystem leases serialize cache reads and publication.
Refresh preserves every archived version. Legacy cache files without archival proof
remain protected originals: reuse is allowed; refresh refuses to overwrite them.
Archive write time records when the assessment bytes were saved, never an invented
historical receipt or classification time. Cleanup currently preserves all normalized
files and their proof sidecars; archival proof alone does not authorize deleting them.

## Intent

Research evidence is immutable and separate from mutable paper/runtime state.
Preserve published backtest results and immutable parameters, including failed or
uncurated results. Remove owned temporary run inputs only after the exact run's
independent quant cleanup approval; discard abandoned unpublished artifacts.

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

Delete disposable run-owned projections and intermediate worker logs only after
the exact run's independent quant cleanup approval. Discard abandoned unpublished
generated configs. Preserve original provider payloads and receipts,
even if a download first occurred during a backtest. Published exploratory results and their immutable
parameters survive even when the run failed. Disposable test outputs are removed
after tests. Read-only source candle datasets and operational caches remain separate
and are not deletion targets of a backtest's workspace cleanup.

## Strategy Lifecycle

Canonical and backtest strategy files remain research artifacts until an explicit
authorization binds strategy ID, semantic version, content hash, dataset evidence,
and a human decision. Folder location and a run-local winner label are not promotion.

The current catalog has 3 research artifacts and no archived artifacts; removed artifacts are listed in `docs/research/retired-strategy-artifacts-2026-11-07.md`. The integrated decision is `RETAIN_RESEARCH`; none is authorized for a new
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
