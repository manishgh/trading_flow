# Refactoring cleanup — 21 October 2026

Core data protection follows the user's latest instruction in [AGENTS.md](../../AGENTS.md#core-data-deletion-requires-specific-approval). Broad cleanup instructions do not authorize deletion of market history, news, earnings, provider receipts, wishlists, saved Finviz lists or operational databases. Challenge even an explicit deletion request, explain the affected datasets and consequences, and obtain fresh dataset-specific confirmation.

## Source cleanup completed

- [x] Remove unused `scratch/ReadNews.cs`; the CLI's bounded, read-only `inspect-data news` replaces its fixed-machine database reader.
- [x] Remove unused `scratch/CheckAlpacaNews.cs` and `.py`; these were standalone provider probes containing literal credentials. Their contents are not copied into this report. Removing the probes does not invalidate credentials.
- [x] Remove the five source/document files in `tools/research/PortfolioEventRunner`: `Program.cs`, `PortfolioEventRunner.csproj`, `FrozenRawProvider.cs`, `MonthlyEventReport.cs`, `README.md`. This dated runner fixed the study's strategies, cohorts, capital and dates. The normal saved-plan/durable-job execution and monthly publisher replace it.
- [x] Two independent read-only inventories found no production, test, solution or CI callers for those obsolete paths. A final source/reference search also found none. Historical reports may still name the old implementation as provenance.
- [x] Preserve `tools/research/AlpacaMetadataCapture`; it remains the implemented route for official quote/trade condition metadata. Its project compiled with zero warnings/errors; the collector was not executed.

Earlier approved cleanup also removed the machine-specific `DbChecker`, `scratch/DbQuery` and unbuilt `src/TestDb` source programs. The shared CLI inspection command requires an existing explicit file and never creates a missing database.

## Data and evidence retained

- [x] No market/news/operator database or source list was deleted by this source cleanup.
- [x] Keep registered strategy artifacts, frozen research configurations, published results, monthly/trade records, immutable parameters and hash-bound source snapshots.
- [x] Keep shared downloaded market history, original provider archives, news stores, credentials, wishlists and Finviz settings regardless of whether a backtest used them.
- [x] Keep the current verification reports, including failed attempts and their later corrections.
- [x] Browser verification uses unique owned synthetic databases and artifact directories. Teardown stops its host before removing those exact directories; it does not use the operator database.
- [x] Backtest workspace tests cover success, failure and cancellation, preservation of a source sentinel and published output, and isolation between two temporary workspaces.

## Retention correction completed

- [x] User approved the concrete correction below on 21 October 2026: "Approve the retention fix".
- [x] Original provider responses now use the accepted configuration's existing `RawRoot`, outside `.run-inputs-*`. Nested preparation/optimizer workspaces reuse that durable writer and original logical roots.
- [x] Alpaca bars/calendar require an archive writer and parse committed response bytes. Every received page, including HTTP errors and 429 retries, is archived before status handling or parsing.
- [x] Completed buffered market responses finish their archive commit independently of caller cancellation; cancellation then stops parsing, retries and emission. Incomplete/canceled wire reads are not claimed to be complete archived responses.
- [x] Automatic original-archive expiry is refused with `NotSupportedException`; it cannot enumerate/delete files or report successful expiry. Existing minimum-retention metadata is not an expiry timer. Operations health wording makes that explicit.
- [x] Clean only private generated projections, candle spills and journals. Preserve outside CSV/frozen inputs, originals, published outcomes and immutable run identities.
- [x] Focused offline verification: **138 passed, zero failed/skipped, eight seconds**; `.tmp/verification-20261021/retention-fix.trx`. The test build also compiled affected Web, CLI, warm-up and shared backend projects. No real provider call or historical research execution occurred.
- [x] Independent architecture, code and plan reviews completed. Architecture verified archive/workspace ownership and removal of automatic expiry. Code review found cancellation during commitment could discard a response; the author corrected it and the reviewer rechecked the four gated cancellation regressions. Plan review confirmed the approved scope and required consumer/recovery checks.

Other old `.tmp` research drivers, parent `outputs` projects and legacy deployment definitions remain untouched where published evidence or deployment instructions still reference them. Zero runtime callers alone does not establish that an evidence file is disposable.

## Approved retention design

Independent source review found two additional details: Alpaca bars/calendar previously parsed HTTP responses without the raw archive writer, and `FileSystemRawArchiveWriter.EnforceRetentionAsync` could delete original date directories. Merely moving existing caches to shared roots would also expose existing cached files to refresh replacement. That is not the proposed fix.

Approved on 21 October 2026 and implemented:

1. Keep original provider responses under the run's **existing configured `RawRoot`**, resolved from the accepted logical configuration before creating any temporary workspace. Reuse that same durable writer for nested optimizer workspaces. Leave outside CSV and frozen SIP/calendar sources unchanged.
2. Pass the existing `IRawArchiveWriter` into `AlpacaMarketDataProvider`. Use the existing `AlpacaRawResponseArchiver` to commit bars/calendar HTTP bodies and receipts before parsing, including error/retry bodies. Archive failure must stop emission; a malformed response must remain available for inspection. News and Finviz already use this mechanism.
3. Keep normalized projections, candle spills and journals inside the private `.run-inputs-*` folder. Delete them on success/failure/cancellation only after their original online responses have been durably archived; synthetic test fixtures remain independently owned. Do not repoint cache-refresh writers at shared originals.
4. Remove automatic expiry of original archive directories. Dataset-specific deletion remains a separately reviewed operation with fresh confirmation. No new configuration framework, API fields, UI controls or strategy behavior is proposed.

Affected production files: `BacktestRunner.cs`, `ConfiguredDataProviders.cs`, `AlpacaMarketDataProvider.cs`, warm-up `Program.cs`, `FileSystemRawArchiveWriter.cs`, and direct constructor consumers if required. Update their focused tests and retention documentation together. Preserve candidate identity and the existing source/time/fill rules.

Verification: fake HTTP bodies and manifests survive success, parser failure and cancellation; archival failure emits no rows; nested runs isolate and delete only their spools; pre-existing source hashes stay unchanged; recovery retains candidate IDs. Re-run the affected provider/workspace/recovery tests and compile their consumers serially. No real provider call or operator-data deletion is needed to verify this correction.

The user approved this concrete design before implementation. No new API fields, UI settings, strategy rules, source memberships or execution assumptions were added. Web/CLI/warm-up constructors now use their existing persistent archive owners; the shared provider factory carries that writer through paper/research composition. The operations health description states that automatic deletion is disabled.


## Verification command and limits

The focused `dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore` selection covered `RawArchiveWriterTests`, `AlpacaMarketDataArchiveTests`, `AlpacaMarketDataProviderTests`, `AlpacaMarketSessionScheduleProviderTests`, `AlpacaCandlePipelineIntegrationTests`, `ConfiguredDataProvidersTests`, `BacktestRunWorkspaceTests`, `BacktestProviderRetentionTests`, `EventPortfolioSourceBindingTests`, `StrategyOptimizerTests`, interrupted/canceled real-runner tests, `AlpacaNewsProviderTests` and `FinvizClientTests`. The exact selection and each outcome are retained in `retention-fix.trx`.

The opportunistic candle-pipeline test previously consulted live credentials and could return without assertions. It now uses fixed HTTP fixtures, preserves its pipeline assertions, and cleans only its unique synthetic archive. All tests in this checkpoint are offline. This is a focused component check, not a new full-suite release or provider-data strategy-performance assessment.

Prepared candles remain the engine input. This approved correction retains original responses as evidence and removes private prepared copies after runs. It does not create or redesign a shared reusable prepared-candle store; the user's later question about that was explicitly discussion only. Existing shared datasets/caches are not deletion targets.
