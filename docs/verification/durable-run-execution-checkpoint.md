# Durable Run Execution Checkpoint

**Status:** Complete; independent final review reports GO

**Date:** 2026-09-16

## Scope

This checkpoint makes backtest, optimization, and paper scheduling durable. The
SQLite job row is the queue and terminal authority; the in-memory dictionaries are
bounded UI projections only.

1. Enqueue persists the complete request before returning success. Config and
   strategy files are captured with hashes and a fixed UTC evaluation cutoff.
2. A verified read lease keeps every captured input immutable for the full attempt.
3. Fixed, configuration-driven worker counts claim jobs through fenced SQLite
   leases. SQLite time, not a worker clock, decides acquisition, renewal, snapshot,
   and completion eligibility.
4. Backtest and optimization recovery replay from the frozen request. A recovered
   paper attempt waits for healthy order-dispatch recovery, synchronizes broker
   orders, and reconciles account exposure before normal execution.
5. Cancellation is persisted. A running handler observes cancellation, drains, and
   only then publishes terminal `cancelled` state.
6. Every attempt writes into a job-ID and lease-token scoped directory. SQLite
   terminal completion is committed before the publication marker; a marker or UI
   projection failure cannot rerun or fail the completed job and is retried by the
   periodic projection refresh.
7. Projection refresh is idempotent across processes. A refresh racing with enqueue
   cannot turn a successful durable enqueue into an API failure, and a missing
   original config cannot hide a persisted job's status.
8. Durable workers are registered last and therefore stop first. Their bounded drain
   budget leaves time for final broker protection, journal flush, and SQLite
   checkpoint services.
9. A transient queue-store failure cannot silently remove a worker. The host logs
   the fault, waits the configured bounded poll interval, and restarts that worker.
10. Broker-command recovery processes bounded pages but probes for residual work
    before publishing readiness. A page limit can therefore delay readiness, never
    hide a recovery backlog.
11. Paper recovery requires both a reconciled account and successful broker-side
    protection repair. A clean position/order diff cannot resume an unprotected run.
12. Recovery categories are isolated within a cycle. A failed exit or dispatch
    repair cannot starve pending cancellation or protective-stop repair work.
13. Reconciliation blocks new entries before protection verification starts and
    leaves that block active when verification throws.
14. Graceful shutdown preserves broker-generated bracket/OCO children as position
    protection, then verifies all remaining broker positions and protective orders.
15. Durable broker-command recovery owns an independent entry-admission block.
    Startup, an active cycle, or any failed category blocks strategy and manual
    entries; only a complete healthy cycle clears that block.
16. The final entry broker-POST boundary owns an atomic admission lease. Recovery
    first blocks and drains admitted entries, may adopt an existing broker order,
    and may ignore only its own recovery block for a fresh retry. Every independent
    reconciliation, protection, stream, risk, or shutdown block remains binding.

## Counterexamples Verified

- Two workers racing for one row produce one lease owner and one execution.
- A worker whose clock is one day fast cannot steal an unexpired lease.
- An expired owner cannot save a snapshot, renew, or publish a terminal result.
- Persisted cancellation prevents a completed result from being published.
- A terminal projection callback failure leaves one completed attempt and no lease.
- Restarted paper work reconciles dispatch readiness, broker orders, and account
  exposure before the runner resumes.
- A new or hung dispatch-recovery cycle makes readiness fail closed immediately.
- A successfully processed capped recovery page remains unready when another
  position-exit or protective-stop command is still queued.
- A clean reconciliation with failed protective-order repair cannot resume paper work.
- A transient durable queue read failure restarts the affected worker after a delay.
- Failure in one broker recovery category does not prevent later safety-critical
  categories from running in the same cycle.
- A protection-provider exception cannot expose an entry-admission window.
- A normal Alpaca bracket child is preserved through shutdown and reaches the final
  broker/protection cross-check.
- A first-attempt paper run or manual order cannot bypass incomplete durable broker
  command recovery merely because it has no job-recovery phase of its own.
- Persisted strategy/operator retries cannot bypass final admission, and recovery
  cannot use its privileged path to bypass an independent safety block.
- Recovery waits for an entry that crossed admission before the block; later entries
  fail before any fresh broker submission.
- Backtest and paper enqueue remain successful when another projection refresh wins
  the in-memory publication race.
- Paper status remains visible after its original source config has disappeared.
- Host shutdown waits for handler drain, then releases replayable work without
  claiming false completion.

## Verification

```powershell
dotnet build TradingFlow.sln --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false
dotnet test src\TradingFlow.Tests\TradingFlow.Tests.csproj --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false --filter "FullyQualifiedName~DurableJobRepositoryTests|FullyQualifiedName~DurableJobProcessorTests|FullyQualifiedName~OrderDispatchRecoveryHealthTests|FullyQualifiedName~OrderDispatchFailureTests|FullyQualifiedName~OrderSynchronizationCoordinatorTests|FullyQualifiedName~AccountReconciliationServiceTests|FullyQualifiedName~ExecutionShutdownCoordinatorTests|FullyQualifiedName~PaperJobServiceTests|FullyQualifiedName~BacktestJobServiceTests|FullyQualifiedName~OptimizationJobServiceTests|FullyQualifiedName~PaperJobRecoveryServiceTests|FullyQualifiedName~DurableFileJobRequestTests|FullyQualifiedName~DurableJobWorkerHostedServiceTests|FullyQualifiedName~ProductionCompositionTests"
dotnet test TradingFlow.sln --no-restore -m:1 /nodeReuse:false -p:UseSharedCompilation=false --filter "FullyQualifiedName!~AlpacaCandlePipelineIntegrationTests"
git diff --check
```

Results on 2026-09-15:

- Solution build: succeeded, 0 warnings, 0 errors.
- Focused durable execution and broker-recovery slice: 97 passed, 0 failed, 0 skipped.
- Full deterministic local suite: 1,590 passed, 0 failed, 0 skipped.
- Diff whitespace validation: passed.

The final independent read-only safety review found no unresolved critical or high
defect and returned **GO**.

The live Alpaca candle integration test is intentionally outside the deterministic
gate. Its latest attempt reached the provider boundary but failed during Windows TLS
credential negotiation before any TradingFlow assertion. That host-specific result
does not substitute for a future provider smoke test.

## Operational Boundary

This checkpoint proves scheduling, replay/reconciliation ordering, cancellation,
lease fencing, and result publication. It does not prove a profitable strategy,
multi-session paper performance, provider availability, or production deployment.
Those remain explicit later-stage evidence gates.
