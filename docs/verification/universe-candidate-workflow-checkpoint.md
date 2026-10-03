# Universe and candidate workflow checkpoint

Date: 2026-09-27. Status: complete for checkpoint 3 in
`docs/remaining-work-checkpoints.md`. Verification tier: component checkpoint.
This is not a release, strategy promotion, or complete Web/Android integration.

## Changes and boundaries

The existing preview/start implementation had four reproduced defects: explicit
as-of was ignored, persisted source JSON did not survive the real projection,
exact retries failed after expiry, and snapshot identity was outside its content
validation. The initial four counterexample tests failed before correction.

- Current discovery uses an omitted/null cutoff and captures its cutoff after all
  source reads. An explicit cutoff rejects later observations, including current
  wishlist reads. Provider timestamps cannot exceed that cutoff. Sources must be
  unexpired at capture/start. Current membership is never backdated.
- Preview identity, cutoff, timestamps, members, source evidence and exclusions
  are hash-bound. Replacing metadata without changing the claimed hash is rejected.
- Candidate start binds the exact strategy ID, semantic identity and content hash,
  execution mode and universe snapshot. Hash case and surrounding request-key
  whitespace are normalized before hashing or deriving identities. Catalog mode
  authorization is still required; research artifacts do not authorize paper/live.
- Request, run, every candidate and creation events now commit in one SQLite
  transaction. Concurrent identical starts yield one complete run; an injected
  candidate-write failure rolls everything back. Exact retries return the durable
  current outcome after expiry or state progression without revalidation/upsert.
  A reused key with different intent returns HTTP 409.
- Run provenance cannot be rebound to another universe or decision run. Existing
  run data is preserved. No migration or production database operation was run.
- Source details now survive actual persistence and API projection. Numeric evidence
  parses independently of operator culture. Unknown completed-candle, publication
  and first-receipt times stay null. News enrichment is bounded by the catalyst's
  evaluation cutoff; mutable ingestion times cannot become first-receipt evidence.
- Open exposure belongs to the candidate linked by the position generation's entry
  intent and matching account/symbol. A pending/rejected order for another candidate
  does not acquire that exposure. A later close from another run clears it. Average
  fill price comes from the cumulative order event, not the last execution price.

The Market Predictor contract is untouched by this checkpoint; predictions remain
advisory. No strategy tuning, provider/broker request, GitHub synchronization,
secret/config change, candle deletion or paper-state mutation was performed.

## API behavior and compatibility

The existing routes remain `/api/v1/universes/preview`, `/api/v1/candidate-runs`,
`/api/v1/candidate-runs/{runId}`, and `/api/v1/candidates/{candidateId}/audit`.
They share `CandidateWorkflowEndpoints`, called from the existing authorized API
group. No new numbered type, file, contract label or route was introduced.

`UniversePreviewRequest.asOfUtc` is nullable. Use null/omission for current source
capture; an explicit historical cutoff requires point-in-time source evidence.
`UniversePreviewResponse.decisionCutoffUtc` records the actual cutoff. Start must
supply a non-future `asOfUtc` at or after that cutoff and an unexpired snapshot.
`MarketEvidenceResponse.latestCompletedCandleAtUtc` and catalyst publication/first
receipt timestamps are nullable. Consumers must display unknown when absent.

Older preview JSON lacking the cutoff/full-content binding fails closed. Capture a
new preview for a new start. Older incomplete start records fail closed rather than
being silently repaired or rewriting progressed candidates. No existing database
rows were migrated, removed, or otherwise changed during implementation.

## Verification

All commands ran from `C:\project\trading_flow`, with heavyweight jobs sequential.
The tests use isolated temporary SQLite databases and stop their loopback HTTP
host in `finally`; no production Web or worker was started.

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~UniverseCandidateWorkflowTests|FullyQualifiedName~UniverseWorkflowIntegrityTests|FullyQualifiedName~CandidateWorkflowEndpointTests|FullyQualifiedName~CandidateAuditIntegrityTests|FullyQualifiedName~CandidateApplicationProjectionTests|FullyQualifiedName~ApiContractSerializationTests|FullyQualifiedName~ConfigCatalogServiceTests|FullyQualifiedName~SqliteDurabilityTests|FullyQualifiedName~ReconciliationPersistenceTests|FullyQualifiedName~EntryDecisionPersistenceTests|FullyQualifiedName~OrderPreviewRepositoryTests|FullyQualifiedName~BacktestCandidateJournalTests|FullyQualifiedName~StrategyCandidateDecisionOrchestratorTests" --verbosity minimal --logger "trx;LogFileName=candidate-workflow.trx" --results-directory .test-tmp/candidate-workflow -m:1 --disable-build-servers -- xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false
```

Result: 149 passed, 0 failed, 0 skipped (35 seconds). Application, Data, Contracts,
Web, tests and referenced C# projects compiled successfully under their existing
compiler/nullable/analyzer settings. Broader persistence/order tests were included
because immutable run validation and exposure projection are shared consumers.
Report: `.test-tmp/candidate-workflow/candidate-workflow.trx`.

The hosted fixture was then strengthened to compare raw HTTP JSON rather than
reserialized contract objects. The affected test was rerun after that final change:

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~CandidateWorkflowEndpointTests" --verbosity minimal -m:1 --disable-build-servers
```

Result: 1 passed, 0 failed. This exercises actual production route handlers with
real SQLite repositories: preview, start, run/audit reads, absent/present evidence,
null fields, expired retry, conflicting retry, invalid cutoff and missing audit.
`src/TradingFlow.Tests/Fixtures/Contracts/candidate_workflow_audit.json` is the
frozen response; its README states the two normalized values and lineage checks.
The test principal, controlled source and strategy resolver are explicit seams.
The real catalog resolver is separately tested against authorized exact research
identities, changed hashes/semantic identities and unauthorized execution modes.

```powershell
dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj --no-restore --verbosity minimal -m:1 --disable-build-servers
```

Android build: succeeded, 0 warnings, 0 errors (1m42s). This was the Debug Android compatibility build, not the later Release/device acceptance gate.

`git diff --check`: passed. No task-owned Web/worker/CLI runtime remains active.

Independent requirements review identified the causality, atomicity, retry,
source casing, nullable evidence and exposure ownership gaps addressed above.
Independent final code/design review found one receipt-lineage issue; it was fixed
and regression-tested. Final reviewer outcome: no remaining actionable findings.
Both review agents finished; no implementation was delegated to them.

Not run: full release suite, strategy replay (strategy evaluation is unchanged),
provider/paper smoke, full authentication/CSRF/cookie acceptance, browser layouts,
Android emulator/device navigation or notifications. These remain the explicit
later checkpoint gates. No Python consumer of these TradingFlow candidate DTOs
was found; the separate predictor wire contract is unchanged.

Next: checkpoint 4 order confirmation/access, then resumable updates and Web/Android
consumer migration. The completed checkpoint does not authorize order submission.
