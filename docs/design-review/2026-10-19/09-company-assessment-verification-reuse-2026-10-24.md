# Company-assessment verification reuse — proposed 24 October 2026

**Approved by the user on 25 October 2026.** Approval covers implementation, offline verification, one actual assessment retry if memory and independent reviews pass, and then the previously authorized twenty-book comparison. The proposal wording below is retained as the reviewed scope; no further routine approval is required within it.

Implementation checklist:

- [x] Record approval; verify all 45 existing backtests are completed and stop the owned Web runtime before editing.
- [x] Stream assessment I/O, reports and comparisons; verify unchanged serialized content. Focused parity/source tests pass in `outputs/company-assessment-reuse-20261025/tests/focused.trx`.
- [x] Reuse verified evidence within each preparation/run while retaining leases, hashes and source checks. Independent source reviews accepted; final runtime/memory checks remain below.
- [x] Complete focused tests: 249 passed, zero failed; large-body/high-fanout memory profile passed separately.
- [ ] Complete full-shaped synthetic memory measurements. Stopped before publication because C: lacked disk headroom; see [verification record](../../verification/company-assessment-reuse-2026-10-25.md).
- [ ] Obtain independent engineering and quant approval of the final implementation and measurements.
- [ ] Retry the full archived-news assessment once, then review its publication and frozen plan.
- [ ] Start the approved twenty portfolios through the normal saved-plan workflow and retain monthly outcomes.
- [ ] Independently review outcomes before cleaning only approved generated intermediates.

**Decision: approve one staged implementation, offline verification and one actual assessment retry, conditional on final engineering/quant review and measured memory passing; then proceed through the already authorized twenty-book normal workflow. No further routine confirmation is required after approval.** Stream the identified temporary allocations and reuse the existing frozen provider within one Start/worker attempt. Changes stay in assessment I/O/comparison and backend preparation/runner plumbing; preserve every source/hash/clock check and all 18,016 rows. This should remove repeated full-packet reconstructions and large temporary document copies. It changes no trading rules, configuration, public contracts or UI. Remaining all-row resident memory means success is not guaranteed; inadequate measured headroom stops the retry and requires a separate design. **Design only: no approval, implementation or retry is implied.**

## Observed failure and proposed behavior

[The full assessment failed](../../../outputs/qualified-recovery-news-mapped-20261024/full-assessment-failure.json) at 16:33:54 UTC after approximately 35 minutes: exit -532462766, stderr `Out of memory.`, last observed working set approximately 1.66 GB. No full publication or economic jobs were created.

Source identifies large allocations: all 18,016 rows stay resident; extraction comparison creates paired full JSON strings; row/source comparisons create paired UTF-8 arrays; publication creates a whole-packet byte array; reads retain packet bytes while deserializing; samples build an all-row string and byte array; blind review serializes all stories at once. Issuer-specific regex reconstruction also costs CPU and allocation. **No phase trace identifies the failing allocation or proves regex caused the OOM.**

Preserve 3,015 original archive rows, deduplication to 2,951 exact-content revisions and all 18,016 tagged issuer assessments. The 3,263 rows concerning the selected 24 stocks do not replace that population. Preserve full original text, model input/hash, offsets, contradictions, Unknown outcomes, v1/v2 replay and every source/model/name/clock check. No classifier, strategy, schema, setting, database, permanent cache or disk-backed/lazy storage change.

## Existing work and reuse boundary

Current chain:

`BacktestPlanService.StartAsync → GetDecisionContextEvidenceAsync → catalog binding`; then, per book, `PrepareCohortAsync → evidence binding → WorkflowRunConfiguration.WriteAsync → binding → BacktestRunInputFiles.Resolve`; then `PrepareCoreAsync → captured-input lease → ValidateCapturedBacktestInputsAsync → Resolve`.

Each resolver invokes `FrozenBacktestEvidence.FromConfig → HistoricalDiagnosticNewsProvider.LoadVerified → CompanyNewsAssessmentManifestStore.ReadVerified`, including all-row deterministic reconstruction and original source/mapping replay. Each catalog binding's summary reloads the already verified assessment. Twenty new books therefore cause approximately **81 provider loads / 122 packet validations / 2.2 million row reconstructions before admission**. Catalog listing, Save/Preview and workers add more. [Detailed accounting](../../../outputs/qualified-recovery-news-mapped-20261024/preparation-performance-review.md).

Reuse the existing `FrozenBacktestEvidence` record/provider directly. One small disposable Web-owned `WorkflowEvidenceVerificationScope` retains that object, its assessment summary and existing read lease for one selected diagnostic package during a Start. Application sees only an opaque disposable token because it references Domain/Contracts, not Backtesting. Pass it explicitly through configuration binding, writing and captured-input resolution. Add no second verified-provider type, lease registry, static/singleton cache or ambient/cross-session reuse.

Open it safely:

1. Discover the existing manifest's declared graph, including assessment, original selection, model policy, issuer mapping, raw news, query evidence and calendar. A small internal streaming dependency reader skips assessment rows and returns declarations only; it never grants verified status.
2. Bound every path under its explicit root with existing no-link/reparse checks. The Web-owned scope acquires the graph directly using existing `DurableFileReadLease` with expected SHA-256 values; Backtesting gains no Application reference or lease registry.
3. Run the **unchanged complete source/clock/assessment verifier under that lease**. Its actual dependency paths/hashes must exactly equal the leased declaration set; omissions/additions/conflicts fail. Retain a reusable object only after this completes.
4. Keep the lease until the operation ends. Every reuse checks canonical root/path, schema, manifest hash, graph hash and live scope ownership. Generated configs must pin the verified graph. Recheck path/link restrictions at reuse boundaries. File size/mtime alone never establish sameness.

Retain existing per-book captures, leases, saved-plan graph comparison, authorization, recipe/strict-evidence fences, cohort/capital checks and transactional/idempotent admission. Only the server scope factory supplies the existing verified object to narrow resolver/runner overloads; those overloads match its complete configured identity. The scope owner checks lifetime and holds the lease through all consumers. DTOs and caller hashes cannot create a token; no `skipValidation` option. Existing no-scope entry points continue fresh verification.

A catalog listing/Preview/Save gets its **own** short-lived scope; reuse the same verified assessment for graph and summary. Preserve current choice availability and summary fields. Unclassified/strict evidence keeps its existing path. No Start borrows a prior listing's object.

Each durable worker first acquires its existing captured-input lease, then opens a **fresh** verification scope whose complete graph matches the durable capture. Dependency resolution, runner and nested workspaces share that attempt's scope. Recovery/new process verifies afresh. Exceptions/cancellation dispose only after children drain; disposed/foreign/mismatched scopes fail rather than refresh. Independent operations never share scope lifetime.

Expected result: approximately **one full packet replay per Start**, but still **one full 18,016-row replay per worker attempt**. Existing file hashing/capture, per-decision `Complete` checks, prices and strategy work remain costly. No sharing across the twenty workers is proposed.

## Bound temporary memory with existing publication components

Reuse `AtomicFileArtifactWriter.WriteStreamExclusiveAsync`, FileStream, SHA-256 and System.Text.Json:

- Stream packet JSON with exactly current options/property order to a uniquely owned staging file under the bounded publication root. Flush, stream-hash it, then exclusively move to the existing content-addressed filename. Existing identical bytes may be reused after streaming comparison; changed bytes never overwrite. Partial staging is not discoverable as publication.
- Hash an open read-shared packet stream, rewind the **same stream**, deserialize and run all existing checks. Eliminate `ReadPublished`'s second parse.
- Replace serialized-string/array equality with streamed SHA-256 plus byte-count comparison using each call site's exact serializer options, including the default options currently used by `SameExtraction`. Still reconstruct extraction and compare every nested value.
- Stream samples and blind-review JSON with existing text, encoding, indentation, ordering and reviewer flags. Indexed enumeration replaces repeated `IndexOf`. Cancellation/failure can remove only the operation's exact owned staging file; preserve originals and all committed artifacts.

This bounds temporary serialization/report buffers by the largest value and serializer buffers. **All-row resident objects and existing source-replay materialization remain. Success is not guaranteed.** No regex/GC tuning or row-spooling redesign is included.

## Files and public interfaces

| Exact implementation surface | Change |
|---|---|
| `Data/Evidence/Sentiment/CompanyNewsAssessmentManifest.cs`; new `CompanyNewsAssessmentDependencyReader.cs` | Streaming I/O/equality and declaration discovery; same verifier/publication. |
| `Domain/News/CompanyNewsAssessmentClassifier.cs`; new `CompanyNewsAssessmentContentComparer.cs` | Bounded comparison, unchanged extraction rules/options. |
| `Cli/CompanyNewsAssessmentCommand.cs` | Stream samples/blind review. |
| `Data/Evidence/Historical/HistoricalDiagnosticNewsProvider.cs`, `HistoricalDiagnosticNewsManifestStore.cs` | Reuse verified assessment summary/graph. |
| `Backtesting/FrozenBacktestEvidence.cs`, `BacktestRunInputFiles.cs`, `BacktestRunner.cs` | Existing verified object; exact-identity resolver/runner overloads and nested propagation; no new provider abstraction. |
| `Application/Jobs/RunWorkflowService.cs`, `RunWorkflowService.Cohorts.cs`, `BacktestPlanService.cs` | Opaque scope factory and explicit backtest-only parameters for GetEvidence/Write/ValidateCaptured. |
| `Web/Services/WorkflowRunConfiguration.cs`; new `WorkflowEvidenceVerificationScope.cs`; `ConfigCatalogService.Evidence.cs`, `ConfigCatalogService.HistoricalNews.cs`, `BacktestJobService.cs` | Existing lease adapter and scoped bind/admission/worker integration. |

Paths above are under `src/TradingFlow.<project>/`. Reuse AtomicFileArtifactWriter and DurableFileReadLease without widening their responsibilities. No new package/project reference. Keep helpers small with purpose comments.

**No route, DTO, database, YAML/JSON schema, configuration, JS/Razor or Android change.** Public preview remains paged and authenticated. Actual news receipt/publication/update, mapping capture and model completion clocks remain distinct from assumed historical availability; reuse proves neither historical availability nor classifier accuracy.

## Verification before actual retry

Use existing focused CompanyNews classifier/evidence/mapping tests, HistoricalDiagnosticNewsTests, DurableFileJobRequestTests, WorkflowConfigurationTests, RunWorkflowIntegrityTests, BacktestPlanTests and deterministic decision/replay fixtures:

- Exact old/new packet bytes/hash, samples and blind-review parity, including Unicode/escaping/nulls/options/order/large bodies. Compare streamed equality with old serialized comparisons for changed nested values.
- Mutated packet/raw receipt/calendar/name/model, omitted dependency, escaping/link path, changed identity, disposed/foreign scope, concurrent operations and cancellation must reject or prevent writes. Same hashes and live leases remain mandatory.
- Assert one replay per Start and worker attempt; fresh replay after disposal/recovery. Preserve captures and per-book checks; compare complete decisions/rejections/orders/provenance with the no-scope path.
- Inject publication failure/cancellation and identical/conflicting destination. Never expose partial or overwrite committed content.
- Run one **isolated synthetic full-shaped workload**: 2,951 stories and 18,016 rows, matching the actual archive's source text-size and issuer-tag duplication distributions, including long-tail bodies and repeated text/spans across rows; matching counts alone is insufficient. Use synthetic content with supported/Unknown cases and deterministic fake model evidence. Exercise build/publication/read/summary/Start/worker sequentially. Record phase time, peak working/private memory, allocation/GC observations, packet size and retained heap after disposal; include a large-body stress case. No inference or core writes.
- Build affected C# projects and obtain independent engineering/quant review. Run heavy checks serially. Android rebuild is unnecessary absent wire/client edits.

Compare measured peak and retained memory with host headroom including FinBERT. If final engineering/quant review and that memory checkpoint pass, perform the single actual assessment retry covered by this design approval, then use the already authorized normal twenty-book workflow after valid publication. No further routine confirmation is needed. If inadequate, stop for a separate design; do not shrink text/population or enqueue jobs as a workaround. The remaining question is measured memory, not an operator storage choice.

**Approve or reject this one staged implementation, offline verification and conditional single actual assessment retry.** Approval is not assumed. The existing twenty-book authorization remains; retry and normal admission follow only after the stated checkpoints pass. No separate routine approval is invented.




