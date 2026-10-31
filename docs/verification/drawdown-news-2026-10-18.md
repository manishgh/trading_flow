# Drawdown observations and categorized News verification

Date: 18 October 2026. Checkpoint: component implementation. Branch: `research/swing-pit-review-20261008`, in the existing isolated research checkout. Prior uncommitted implementation work and published research results are preserved.

## Delivered behavior

The shared daily decision kernel now records versioned drawdown anatomy and pressure observations, including non-triggered evaluations. Its audit keeps the latest prior 20-session peak, ordered subsequent low, pre-decline and lagged ATR, depth fractions, close location, normalized one/three-session price moves, true range and volume ratio. Optional configuration uses explicit inclusive bounds; no strategy file or active threshold was selected. Absent configuration retains the previous strategy identity and semantic decision bytes. Observation audit fields are outside that semantic digest.

A generic episode census freezes the onset and original peak, uses non-overlapping outcome windows, retains unrecovered observations and releases labels only when their declared horizon has matured. Historical eligibility requires stable identity, adjustment-version and exact-session evidence. Current runtime adapters do not supply that contract, so history gates remain unknown. Sector and evaluated-event gates likewise remain unavailable until their point-in-time inputs are implemented. Unknown required qualification rejects an opportunity; it cannot silently retry into a later entry. The existing candidate, portfolio and fill audit joins are preserved.

This implements the feature-only checkpoint in [the research plan](../research/drawdown-qualification-research-plan-2026-10-18.md), with exact measurement and episode semantics in [the implementation contract](../research/drawdown-qualification-implementation-2026-10-18.md). It does not complete the historical coverage/partition audit or run the planned economic treatments. No new return, profitable cutoff or strategy promotion is claimed. The existing BKSY/DIOD gaps and first-entry-interval protection limitation remain unresolved.

News now has multi-label reading categories: Company, Sector, Financial / macro, Government / policy, Global events and Other market, plus All news. Eleven independent sector-topic filters use the same expression rules as database filtering. Topics are inferred display metadata, separate from existing provider/evidence classification and strategy catalyst admission. Direct article symbols remain separate from broad MARKET context; no affected-stock relationship is invented.

Category and sector filters run before SQLite counting and pagination. News UI sends an explicit category; broad categories can include general-market context beside selected wishlist/Finviz stories. Omitted-category API calls preserve their prior stock-scope behavior. An explicit ticker or `stockLinkedOnly=true` restricts to direct symbols. Existing `linkedOnly=true` continues to mean the article has a URL, independently of stock links. Private sources still require authorization before membership/account reads.

The News page keeps the rolling four-hour default and 30-second refresh, side stock list, independent pagers and exact sign-in return URL. Categories wrap at narrow widths. Primary controls remain visible; More filters contains advanced controls, automatically opens for active choices and retains all named form values. Shared server/JavaScript rendering shows topic labels and protects group actions from MARKET or inferred stocks.

Public context ingestion adds UN News and US Government Publishing Office presidential documents alongside existing sources. Publication and receipt timestamps stay separate; date-only, update-only, future and invalid publication observations are rejected by the new parser. RSS downloads have a two-MiB cap, prohibited DTDs/external entities, a full-request/body deadline, bounded rows and per-source cache lifetimes. A failed or stalled source does not discard healthy source results. This feed is display evidence, not a historical classified-catalyst dataset.

## Independent agents and resolved findings

All newly delegated implementation/review agents used the user-requested `gpt-6.1-sol` with high reasoning and non-forked focused prompts. At most two reviewers were active together; heavyweight checks ran serially.

- `implementation_plan_review` approved the bounded observation and News scope, requiring exact history semantics and explicit missing evidence before economic runs.
- `news_design_review` specified the category navigation, sector filter, sidebar and display/evidence distinction. Its visual review requested More filters and sentence-case feed descriptions; final populated screenshots were approved with no material layout issue.
- `drawdown_engine_implementation`, `news_category_backend`, and `news_category_ui` implemented disjoint areas in parallel; root integrated serialization, public sources, live smoke and browser checks.
- `drawdown_code_reviewer` caught missing registration of the YAML mapping and missing standalone snapshot feed/adjustment checks. Both were repaired with regressions. Final review approved the feature-only checkpoint; prefix/census rescanning remains a performance consideration for later large studies.
- `news_code_reviewer` caught a body-read timeout gap with ResponseHeadersRead. A per-feed deadline now spans headers and body, with a stalled-stream regression. A final audit also moved receipt sampling after body completion; an advancing-stream clock regression proves the corrected timing. Final review found no remaining P1/P2 issue. Existing article-link semantics were also preserved with a separate stock-link control.

## Final checks

The final focused C# command builds the affected projects and runs 207 distinct checks; result: **207 passed, zero failed/skipped**, approximately 21 seconds. Coverage includes new observation/qualification/configuration, real SQLite category filtering/count/paging, UI adapters, public-feed parsing/deadline, existing News queries/sources/presentation, candidate orchestration/state, daily timing, fine execution, order planning, live-runner consumers, persistence and mobile entry admission.

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter 'FullyQualifiedName~DrawdownQualificationTests|FullyQualifiedName~DrawdownConfigurationRoundtripTests|FullyQualifiedName~NewsCategoryTests|FullyQualifiedName~NewsCategoryUiTests|FullyQualifiedName~PublicContextNewsTests|FullyQualifiedName~OfficialMarketNewsProviderTests|FullyQualifiedName~NewsStockSourceTests|FullyQualifiedName~GroupNewsQueryTests|FullyQualifiedName~NewsPresentationTests|FullyQualifiedName~StrategyDecisionKernelTests|FullyQualifiedName~StrategyCandidateDecisionOrchestratorTests|FullyQualifiedName~StrategyCandidateStateMachineTests|FullyQualifiedName~MixedTimeframeDecisionTimingTests|FullyQualifiedName~SwingFamilyConfigurationTests|FullyQualifiedName~FineExecutionReplayTests|FullyQualifiedName~EntryDecisionPersistenceTests|FullyQualifiedName~LiveRunnerIntegrationTests|FullyQualifiedName~MobileAutomationServiceEntryGateTests|FullyQualifiedName~BacktestSimulationCharacterizationTests|FullyQualifiedName~CompletedBarExecutionPlannerTests|FullyQualifiedName~StrategyOrderPlannerTests|FullyQualifiedName~NewsFeedServiceTests|FullyQualifiedName~EarningsNewsRepositoryTests|FullyQualifiedName~StrategyCandidateState|FullyQualifiedName~SharedDecision' --verbosity minimal
```

Final browser verification: **19 passed** across the new categories, existing stock-source and public-workspace specifications, then **2 passed** for the existing news-hub filter regressions. Tests exercise real isolated HTTP/Razor/API paths, exact private-source authorization, distinct link controls, defaults/active advanced filters, preserved live-refresh reading protection, and populated desktop/sidebar and narrow layouts. No mutation test targets an operator URL.

```powershell
cd tests/ui
npx playwright test specs/news-categories.spec.js specs/news-stock-sources.spec.js specs/public-workspace.spec.js --workers=1
npx playwright test specs/trading-capabilities.spec.js --grep 'news hub' --workers=1
```

The runs used unique owned data roots, runtime trading disabled by UI-test composition, market feeds disabled and the built-in isolated test account. JavaScript syntax checks passed. Final CRLF-aware `git diff --check` passed. The temporary test host required execution outside the restricted process sandbox after its first startup/realpath attempt failed; automatic approval permitted the isolated action. No approval review rejection occurred.

Earlier checks found one test construction compile error, three strict-YAML roundtrip failures and a hash assertion that reserialized JsonElement instead of hashing the exact embedded bytes. These were fixed before the final combined pass; no assertions or production identity requirements were removed. Initial browser/layout passes are superseded by the final refinements, not added to the final totals.

## Real public-source evidence

Read-only HTTP probes returned RSS 200 responses from the [UN News feed](https://news.un.org/feed/subscribe/en/news/all/rss.xml) and [GovInfo presidential feed](https://www.govinfo.gov/rss/dcpd.xml). GovInfo documents its feeds at [RSS Feeds](https://www.govinfo.gov/feeds).

A separate public-read smoke executed the production OfficialMarketNewsProvider, then SqliteNewsFeedRepository and production category predicates in an in-memory database. At 19:55 UTC on 18 October, it retained 8 UN News items and 10 US government documents within 72 hours. All seven category queries succeeded. The latest observed publications were 17 October for UN and 16 October for government, so the four-hour queries correctly returned zero; receipt time did not make those publications recent. This verifies those public paths, not current Alpaca/Finviz coverage or operation of an operator-hosted page.

No broker services started and no operator database was opened by this smoke. Raw feed payloads stayed in memory. The receipt is `outputs/drawdown-news-20261018/public-provider-smoke.json`; archived executed smoke source and project are retained alongside it. Populated UI screenshots in that directory contain explicitly synthetic presentation fixtures, not live-news evidence.

The retained source-evidence inventory references all 25 original names/five cohorts and hashes the prior capture receipt. It is explicitly an inventory of historical published receipts, not a fresh candle/sector/news coverage audit. It preserves prior unavailable/warmup and SOXL instrument dispositions and records zero new economic attempts.

## Retention and remaining work

Preserve this source checkpoint, published smoke receipts, screenshots, immutable configuration contracts and historical results. Owned UI test DB/WAL/cache/run/artifact directories are removed by their cleanup reporter after host shutdown. The temporary public-smoke project/build outputs are removed after review; only its published source/receipt are retained. No operator lists, screens, news records, secrets, caches or historical results are deleted or reset.

Before economic filtering runs: finish the actual source/partition audit, freeze exact bounds/sample floors and resolve SOXL admission; supply historical sector/event and runtime history evidence where required; maintain complete matched controls and all existing execution limitations. Classifications from this News display cannot be used as validated event features. Broad risk/exit optimization, Android, GitHub synchronization, deployment, economic runs and trading activation were deliberately not performed in this component checkpoint.
