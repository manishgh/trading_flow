# Audit Streaming Checkpoint

Date: 2026-09-15. Status: complete. Independent plan and code/design reviews found
no unresolved critical or high-severity findings after the corrections below.

## Implemented

- `IArtifactWriter` publishes through a private temporary file, durable flush, and
  exclusive atomic move. Failure or cancellation cannot expose a partial destination.
- Execution and candidate evidence is streamed during a run instead of retained as
  unbounded managed arrays. Each raw journal has a durable SQLite semantic index used
  for bounded lookup and chronological merge; raw journals remain manifest-referenced
  evidence while derived indexes are removed only after successful publication.
- Execution-journal records are limited by their fully serialized UTF-8 size to
  64 KiB. Oversized records fail before any bytes are appended, and an append failure
  latches the journal so later writes or exports cannot hide lost evidence.
- Candidate transition batches validate and persist before mutable state is published.
  Candidate identity remains unchanged through decisions, hypotheses, simulator events,
  fills, terminal failures, and completed trades.
- The chronological coordinator processes flattened bars by availability time and then
  event time. A delayed event-time inversion for the same symbol/timeframe is rejected,
  while independent bars sharing an availability timestamp remain valid.
- A speculative accepted candidate is not a completed trade. Fill side, quantity,
  price, fees, and risk-reducing status come from simulator evidence; incomplete
  exposure at end of data is retained as a structured terminal execution failure.
- Buy and sell fixed fees are modeled separately. Partial fills, replacements, and
  protective sibling orders share a fee-group identity so a fixed fee is not charged
  repeatedly for one economic order.
- Publication uses a last-written manifest as its commit marker. It requires exactly
  one each of `backtest_result`, `candidate_decisions`, `candidate_hypotheses`,
  `unified_portfolio_execution`, and `unified_completed_trades`, with confined relative
  paths, artifact-set identity, SHA-256, byte length, and record count.
- Manifest verification builds a temporary semantic index and reconciles every
  completed trade to execution fills: candidate identity, side, quantity, weighted
  fill price, fees, chronology, gross profit, and net profit.
- Terminal execution failures and failed strategy/ticker work items make economic
  results incomplete. Top-level, unified, and per-strategy profit, return, drawdown,
  win/loss, and completed-trade claims are then neutralized rather than published as
  trustworthy economics. Failure evidence remains available for diagnosis.
- Explicit finite limits now bound retained pipeline bars, combined replay/benchmark
  bars, strategy/ticker work items, retained candidates, candidate history, and order
  contract history. Zero no longer means unlimited for candle-pipeline retention.
- Prepared benchmark bars are reused when they are already part of market state, so
  retention accounting and memory do not double-count an alias of the same data.

## Verification

Focused integrity and retention suite:

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~BacktestArtifactIntegrityTests|FullyQualifiedName~BacktestArtifactProjectorTests|FullyQualifiedName~BacktestRunnerExecutionQualityTests|FullyQualifiedName~ChronologicalBacktestExecutionCoordinatorTests|FullyQualifiedName~DeterministicExecutionSimulatorTests|FullyQualifiedName~CandlePipelineEngineTests|FullyQualifiedName~BacktestSimulationCharacterizationTests" -m:1 /nodeReuse:false -p:UseSharedCompilation=false
```

Result: 105 passed, 0 failed, 0 skipped.

Full deterministic local suite, excluding the provider-dependent Alpaca integration:

```powershell
dotnet test TradingFlow.sln --no-restore --filter "FullyQualifiedName!~AlpacaCandlePipelineIntegrationTests" -m:1 /nodeReuse:false -p:UseSharedCompilation=false
```

Result: 1,543 passed, 0 failed, 0 skipped. `git diff --check` also passed.

The live Alpaca integration was attempted separately. Network access succeeded, but
Windows TLS authentication failed before a provider assertion with
`AuthenticationException: No credentials are available in the security package`.
This is retained as an environment limitation and is not counted as deterministic
application evidence.

Tests cover atomic publication races, cancellation, journal failure latching,
oversized multibyte records, chronological replay, delayed-bar inversion, bounded
bar/candidate/work/order retention, asymmetric fees, partial end-of-data exposure,
manifest mutation and traversal, required artifact multiplicity, fill/trade economic
reconciliation, nested-result suppression, and failed-work-item suppression.

The isolated resource drill measured 10,000 records, 2,118,890 disk bytes,
394,408 retained managed bytes, and one retained preview event. Its acceptance bounds
are 1-16 MiB disk, at most 8 MiB retained managed growth, and exactly one preview.

## Boundaries

- Final artifacts are individually atomic; the valid last-written manifest is the
  artifact-set publication boundary.
- Raw NDJSON and its SQLite index are not a cross-file transaction. Any failure between
  raw flush and index commit fails the run and preserves evidence for reconciliation.
- Operating-system disk exhaustion remains an environment drill; deterministic writer
  and journal failure injection covers the application response.
- No strategy parameters, broker orders, secrets, Web UI, or Android behavior changed
  as part of this checkpoint.
