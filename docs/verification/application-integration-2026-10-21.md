# Application integration — 21 October 2026

Implementation follows the approved [P-01–P-09 plan](../design-review/2026-10-19/07-implementation-plan-2026-10-21.md). The backend integration checkpoint passed. Actual phone checks and external deployment checks have separate status below. The subsequent approved provider-download retention correction passed 138 focused offline checks; no real research collection is authorized by this checkpoint.

## Completed checks

| Check | Observed result | Limit of this evidence |
|---|---|---|
| Final focused .NET integration | **464 passed, zero failed/skipped, 14 min 51 s**; `.tmp/verification-20261021/verified-application-integration.trx` | Real application/runner paths with synthetic provider edges. This is not evidence of market profitability or a full-suite release check. |
| Browser backtest and Desk workflows | **24 scenarios passed**: 23 in the combined run, then one stale placeholder expectation corrected and its focused rerun passed | Isolated Web host and synthetic storage. Source/profile capture, save without start, retry keys, strategy refresh and 412/1280/1600-pixel layouts. No operator/ngrok database used. |
| Python sentiment service | 11 offline tests passed | Fake Transformers/model files; no actual model weights loaded and no classification-accuracy claim. |
| Event execution profiling | Two opt-in tests passed; repeated complete state and fill/fee hashes matched | Debug build, deterministic fixtures, no provider data or trading orders. |
| Final Android builds | **Debug passed in 1 min 26.17 s; Release passed in 1 min 16.93 s; zero warnings/errors** | Includes final Paper/Warmup lifecycle and archive retry fixes. Merged Release manifest has `usesCleartextTraffic=false`. No installation or phone interaction is claimed. |

The .NET checks cover saved matrix admission (50 stocks / groups of ten / four strategies; 30 / five / two; 25 / five / two; leftovers), retry/recovery, effective settings, source binding/calendar gaps, monthly accounting, actual account positions, warm-up persistence/archive failures, optimizer bounds, sentiment identity, broker activities, entry reservations and order recovery. The final shared API/client tests also execute every book in the 20-, 12- and six-portfolio matrices through the real durable handler, shared engine, result reopening and monthly publisher. Additional one-book native and SIP cases and SIP host-interruption recovery verify fills, stable candidate IDs, frozen source consumption and publication after restart. Synthetic fixture results do not establish market profitability.

Tests use unique temporary storage and fake providers. Browser teardown reported deletion of its database, cache, run files and screenshots after its host stopped. Verification reports are retained checkpoint evidence; operator storage, source lists, market history and published research results were not reset.

## Performance measurements

Environment: Windows 10.0.26200, .NET 10.0.11, x64, workstation GC, Debug build. No timing threshold gates the tests. Fixture construction and hashing are excluded from the direct-book allocation counters.

| Fixture | First / repeated elapsed | Allocations | Interpretation |
|---|---:|---:|---|
| Five-symbol normal coordinator, 50 completed parent entries, 5,250 frames | 171 / 79 ms | 27.4 / 26.8 MB | Includes the normal coordinator, which avoids calling the matcher for closed books on quote-only frames. |
| Direct matcher, ten completed parents, 5,000 subsequent quote frames | 441 / 436 ms | 197 MB each | Deliberately forces calls the coordinator normally skips. |
| Direct matcher, 100 completed parents, same quote tail | 1,559 / 886 ms | 1.61 GB each | Demonstrates copying cost with retained history, not normal full-run runtime. |
| Direct matcher, 500 completed parents, same quote tail | 4,865 / 4,607 ms | 5.59 GB each | Historical order/receipt maps remain retained; no pruning or fill assumptions were changed. |

The largest direct fixture's test-host peak working set was about 200 MB. That is a process-lifetime high-water mark, not retained-memory growth or a per-fixture delta. Normal-coordinator fill/fee hash: `894add696c948b6c7a8e7dd6fd5c46adf53f8a8b476405083c7c5f1bd852bc72`.

Reproduce with `TRADINGFLOW_PROFILE_EVENT_EXECUTION=1` and `dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --filter FullyQualifiedName~EventExecutionProfilingTests`. The fixture source is checked in under the test project; output directories contain no executable research source.

## Review corrections

- Rejected and losing cohort attempts clean their generated private configurations/strategy copies, including failures before a durable job exists. Cleanup excludes shared source/calendar archives and accepted job inputs.
- Frozen calendar coverage rejects a missing trading session between entry and exit. Physical archive presence alone is insufficient.
- Intraday final-month fills after the last available closing valuation withhold that month's equity, return and closed-trade count. Earlier complete months remain visible.
- Operational daily/open-position counts include account-wide external pending entries and actual first entry fills. Unknown roles or incomplete/stale histories reject new entries. Exits remain independent of entry capacity.
- Nested closing broker legs require exact parent/account/symbol/side ownership; symbol or buy/sell side alone does not prove whether an order opens or closes a holding.
- Warm-up Add + Run now retains retry identity; expected scheduled work-budget rejection does not end future nightly scheduling.
- Previewed profile hashes must survive Save, and clients must show the selected profile's actual values. Android must discard old connection/page responses and invalidate editable previews after form changes.

## Remaining before completion

- Android phone interaction needs an available device. Final Debug/Release builds and independent source review passed; compilation does not establish device behavior.
- Disposable Docker image exclusion checks and Kubernetes/Bicep validation. Docker/kubectl were not available in this session; no external deployment ran.
- A real provider-backed research run still needs its separate approved research design. The data-retention correction is now implemented and tested; [cleanup evidence](refactoring-cleanup-2026-10-21.md) records its 138 passing checks and preserved-data boundary.
- Real historical strategy reruns, human news labeling, model-quality claims, trading authorization and orders remain separate from these implementation checks.

## Final review corrections

Independent code/design and quant/requirements reviews found and drove these additional corrections. They reviewed the follow-up source; the final 464-test report verifies the backend corrections.

- Reopening a real completed backtest failed because `UniversePromotionEligibility` used a constructor collection type different from its serialized property. The constructor now binds the actual property type and a round-trip regression covers both eligible and ineligible results.
- A private local SQLite backup inherited its source's WAL header. The backup now changes only the copied destination to DELETE journal mode before closing. Publication refuses companion journals, and failure cleanup removes the attempt's exact staging filenames. The source remains in WAL mode.
- Preview/Save now resolves exact strategy identities using the same resolver as Start; an unknown or changed strategy cannot be saved as an executable plan.
- Standalone monthly artifacts retain original capital and the actual saved plan/cohort execution. Web and Android show that context from the execution, without changing monthly denominators.
- Current assessments preserve news-query bounds and benchmark rows through the final decision cutoff. Old benchmarks or an uncovered news interval produce a concrete missing-evidence reason. Fresh-news vetoes request their entire configured horizon. Ordinary News page/provider reads remain available.
- Candidate identities no longer include random per-attempt market spool directories. The new real SIP interruption test compares candidates before and after host shutdown/restart, as well as the frozen request, source hash, actual fills and monthly publication.
- Research/CLI/Web raw providers now share feed, article-budget and sentiment-timeout construction. Standalone tools use explicit environment credentials; Web supplies its own host configuration. No shared code searches another application's local settings.
- The order-entry method was split into account validation, authorization, reservation and dispatch operations without moving their event order. Exit/protection recovery protocols retain their existing paths.

Current news-dependent strategy assessments have a specific limit: a REST query ending at time T cannot establish that no article appeared between T and the later evaluation time. The assessment names that UTC gap and withholds a passing news check. This implementation does not add a continuous news watermark or claim complete current news coverage from an earlier download.

The first real matrix run executed its books but failed when reopening results; its initial report records 102 passed and seven failed tests, including the JSON reopen defect and two fixture problems. That report is retained as failed evidence, not counted as final acceptance. The final 464-test run now passes with the corrected code. The earlier failed attempt's 50-stock/four-strategy case took about 10 minutes 36 seconds. In the final passing report, that case took 9 min 12.59 s, the 12-portfolio case 4 min 2.43 s, and the six-portfolio leftover case 1 min 25.96 s. These synthetic fixture timings do not forecast provider-data research runs.


Independent Android review also found and resolved a disabled archive-retry button after transient failure. Paper/Warmup now reject responses from earlier accounts/page visits, clear old account state when returning to a hidden page, cancel hidden-page calls, and retain same-account uncertain request keys. The reviewer rechecked the correction; device behavior remains unverified.


Source cleanup and retention scope are recorded separately in the [cleanup manifest](refactoring-cleanup-2026-10-21.md). The user approved the runtime correction for original provider downloads. It is implemented and passed 138 focused offline checks after the earlier 464-test checkpoint. These counts overlap and must not be added. The affected backend hosts compiled; API contracts and Android models did not change in this follow-up. No real provider data or operator database was touched.


The retained metadata collector also compiled successfully with zero warnings/errors using `dotnet build tools/research/AlpacaMetadataCapture/MetadataDriver.csproj --nologo -v minimal --ignore-failed-sources`. It was not executed; no metadata/provider collection occurred. Final CRLF-aware `git diff --check` passed. No operator runtime was restarted and no GitHub sync occurred.

## Prepared-candle reuse follow-up

The separately approved [design 08](../design-review/2026-10-19/08-prepared-candle-reuse-2026-10-21.md) is implemented and offline-verified. Immutable prepared versions are retained at the configured shared normalized root; one selected-data manifest is bound for all books before execution. API/Web/Android display the same committed evidence. The follow-up has 262 distinct .NET cases with passing latest outcomes, 14 browser scenarios with passing latest outcomes, and successful Android Debug/Release builds (zero warnings/errors). This includes actual 20-, 12- and six-book comparisons, direct/prepared trade and monthly parity, optimizer reuse, SIP recovery/calendar conflict, and rejected-input cleanup. These counts overlap earlier checkpoints and must not be added. See the [specific receipt](prepared-candle-reuse-2026-10-21.md) for commands, initial fixture failures/corrections, actual timings and limits. No operator collection, trading, core-data deletion, external deployment or phone interaction was performed.
