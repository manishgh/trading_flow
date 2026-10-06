# Dataset isolation and run cleanup — 6 October 2026

This component checkpoint supersedes the earlier News runtime and dataset details.
Android remains excluded. It is not a product release or trading activation.

## Cause and approved cleanup

The review Web host had been pointed at `.tmp/ui-tests/phone-data`, a browser-test
database containing generated wishlists and saved screens. Selecting that database
for the operator without asking was a mistake. The original operator database was
found at `C:\project\trading_flow\data\tradingflow.db`; its curated lists were not
the test lists displayed by the old host.

The user explicitly requested a reset without recovery copies, keeping Long,
Volatile and genuine Finviz presets. In the canonical database:

- `long` retains MU, SNDK, SPOT, STX and WDC: five existing stocks.
- `volatile` retains 25 existing stocks. Only UI2T, whose persisted display name
  was “UI checkpoint symbol,” was removed as a proved test entry.
- The two saved Finviz queries retain their exact names, IDs and filters:
  Mean revision snapback and Mean revision Average pullback.
- Other lists and old run/job/audit/news/earnings/runtime records were cleared.
  The initial reset removed 119 SQL run records. No backups were created.
- Existing operator credentials and schema/sign-in infrastructure were preserved.
  The supplied existing password passed sign-in; no credential was changed.

The user subsequently clarified that backtest **results should be retained but
their temporary data should not**. Remaining published research packages, result
parameters, and policy/evidence databases are retained. The reset is not rerun.
Provider caches, old input folders, and generated UI-test databases were removed,
including the original checkout's `.tmp/ui-tests` tree. Two obsolete root/Web
SQLite files were deleted only after inspecting their schemas and excluding the
canonical database. The remaining database files are the canonical database and
the research evidence catalog/strategy-authorization infrastructure. These are
used infrastructure, not browser-test database copies.

## Prevention implemented

Playwright owns a fresh UUID data root and artifact root for every invocation.
It refuses an external `TRADINGFLOW_UI_BASE_URL`; its child host has blank provider
keys. `tests/ui/cleanup-reporter.js` runs after host shutdown and reporter output,
validates its exact owned namespace and physical path, and deletes generated
database/WAL/cache/run/artifact files on success or failure. Failed test attempts
also demonstrated cleanup. Tests now create their own necessary wishlist fixtures
instead of relying on leftovers from earlier tests. Checked-in deterministic
fixtures remain source code.

`BacktestRunWorkspace.ExecuteAsync` removes each actual backtest's unique
`.run-inputs-*` directory in `finally`, including failure and cancellation. It
retains published outputs, manifests, trade/audit results, resolved parameters and
read-only CSV sources. Each runner uses its own candle store and raw archive
writer, rather than the shared operator store. Optimization owns one workspace
across preparation and all permutations. Cleanup failures are logged and cannot
silently replace a primary run failure. Generated configs live under the data
root; both audit readers admit only canonical or explicitly generated config
roots with path/reparse checks.

Backtest runners do not write wishlists. Preparation revision lists remain
immutable evidence and are excluded from ordinary wishlist collection queries.
News and desk choices contain exactly two named groups: **My wishlists** and
**Finviz lists**. Finviz's standard screens and the two genuine saved screens
belong together. Aggregate choices are ungrouped. News shows a plain stock list
with wishlist names and selected Finviz membership; it reuses the same resolved
membership snapshot as the article filter. The default wishlist desk contains
wishlist members only, without adding unlisted account holdings.

## Verification

Component tier, affected compiler/build checks and focused tests:

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~NewsStockSourceTests|FullyQualifiedName~GroupNewsQueryTests|FullyQualifiedName~DeskReadModelTests|FullyQualifiedName~WishlistRepositoryTests|FullyQualifiedName~PreparationWorkflowTests|FullyQualifiedName~PreparationEndpointTests|FullyQualifiedName~UniverseWorkflowIntegrityTests|FullyQualifiedName~RunConfigWriterTests|FullyQualifiedName~BacktestRunWorkspaceTests|FullyQualifiedName~BacktestJobServiceTests|FullyQualifiedName~BacktestSimulationCharacterizationTests|FullyQualifiedName~StrategyOptimizerTests|FullyQualifiedName~OptimizationJobServiceTests|FullyQualifiedName~BacktestPortfolioIntegrationTests"
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~AuditEquityReaderTests|FullyQualifiedName~AuditWorkflowReaderTests|FullyQualifiedName~BacktestRunWorkspaceTests|FullyQualifiedName~StrategyOptimizerTests|FullyQualifiedName~OptimizationJobServiceTests"
cd tests/ui
npx playwright test specs/news-stock-sources.spec.js specs/group-news-earnings.spec.js specs/public-workspace.spec.js specs/watch-desk.spec.js specs/trading-capabilities.spec.js specs/workspace-navigation.spec.js specs/workflow-integration.spec.js
```

Results: **116 component tests passed**, then **19 follow-up tests passed** after
the generated-config audit-reader fix; the slices overlap. **69 browser tests
passed**. The successful browser run deleted its generated database and artifacts;
both owned namespaces were empty afterward. Earlier failures exposed tests that
depended on retained data; those tests now own their fixtures. JavaScript syntax
and `git -c core.whitespace=cr-at-eol diff --check` passed. Generated test reports
are disposable; this prose retains the conclusions.

Requirements review and independent source review closed all findings, including
config-root admission, paused endpoint aliases, provider-store ownership, test
cleanup confinement and updated source labels. No full-suite/product-release
claim is made. The separate previously recorded Market Predictor v1/v4 fixture
mismatch remains a release check, not a reason to change its assertion here.

## Actual operator Web verification

The Web snapshot at `.tmp/ui-tests/canonical-web-20261006` runs on port 53019,
PID 38352, using the original canonical data root above. UI test mode is **false**;
`TRADINGFLOW_DISABLE_RUNTIME=true`. Ngrok PID 87292 forwards the existing d88a
public address to that port. This host does not seed demo data. Runtime workers,
fresh run starts and broker mutations are paused; valid-CSRF mutation requests to
the order/position/automation/paper-run aliases returned 403. There were no broker
orders or strategy promotions. Read-only source resolution remains available.

API checks authenticated the existing operator and verified two lists, five/25
stocks, two saved presets, exactly two groups, stock-list markup and paused
mutations. The user's tab initially retained the older signed-in document despite
refresh. Navigating via its visible Clear link loaded the new public page, showing
that its old sign-in session was no longer valid against the canonical database.
Signing in with the existing account and opening News then displayed the new
dataset. Selecting All wishlist stocks and Apply displayed all **30 stocks** with
list names. DOM inspection confirmed only My wishlists and Finviz lists; all test
list/screen names were absent. Both genuine saved Finviz selections were also
checked in that tab: snapback returned LQDA, MGM and NESR; Average pullback returned
COP, CRM, EL and PFE at this observation. Membership changes with the market and is
not a fixed expected count. The tab was returned to All wishlist stocks.
A screenshot is saved in the task output at
`outputs/ui-review/cleaned-dataset-user-tab.png`.

News currently has no stories because stored market/news records were explicitly
cleared and collection remains paused. This is an empty feed, not invented news.
Web and ngrok remain running at the user's request. Original dirty source work,
secrets, checked-in strategies and the separate predictor checkout were not reset.

The existing [desk checklist](trading-desk-plan-status-2026-10-05.md) and
[Azure deployment plan](../integration/azure-deployment-plan-2026-10-05.md) remain
the scope references. Android/Firebase delivery, broker execution acceptance and
Azure provisioning are outside this cleanup checkpoint.
