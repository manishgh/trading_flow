# Workspace UI redesign — 8 November 2026

## Approved scope

The user approved a substantial redesign using the quieter Influence interface as a visual reference: a persistent sidebar, white surfaces, soft gray background, subtle borders, violet accents, concise copy and progressive disclosure. This is a presentation and workflow-composition change. It does not change strategy calculations, admission, risk checks, execution rules, data collection, persistence contracts or lifecycle authorization.

## Six destinations

| Destination | Entry route | Purpose |
| --- | --- | --- |
| Desk | /Desk | Followed stocks, strategy checks, holdings and market context |
| Trading | /Orders | Account positions, order journal and reviewed order ticket |
| Strategies | /Paper | Authorized paper runs, experiments and research/backtest access |
| Earnings | /Earnings | Reporting calendar, result assessment and breakout evidence |
| News | /News | Live stories, topic navigation and source membership |
| Stock groups | /Wishlists | Curated stock groups and saved discovery sources |

Settings retains system health, warm-up, user administration where authorized, environment selection, appearance and account actions. Detail routes retain their parent destination. Public navigation remains News and Earnings with sign-in access.

## Unified Trading

Trading brings account holdings, the existing order journal and reviewed ticket into one workspace. Broker holdings include positions without a local strategy run. Missing account reads remain unavailable; they must not appear as zero holdings or flat profit. Broker protection distinguishes protected, unprotected and unknown states. Order review, preview, confirmation, cancellation, environment locks and authorization continue through their existing controls and contracts. The legacy Positions and Order Ticket routes remain available.

## Reused contracts and behavior

- Desk continues through its existing /api/desk reads/stream and strategy-assessment contracts.
- Trading reuses /api/v1/running-trades?source=all, /api/v1/orders and the existing order context/preview/confirmation flow. UI composition does not introduce a second decision or execution path.
- News retains one GET filter form, category/sector/source semantics, pagination query state and the #news-live-body refresh boundary. Existing live refresh pauses while a reader edits filters or inspects evidence.
- Earnings retains every field name, DOM ID, data hook and form association. Date scope, ticker search and result assessment stay visible. Secondary date/session/cap/sector/evidence controls move into a disclosure, which opens when a restored URL carries a secondary constraint. Counts, result assessment, breakout assessment, schedule certainty and advisory authority remain distinct.
- Stock groups and strategy run forms reuse their existing APIs, validation, review and authorization. Discovery membership and advisory assessments do not grant order authority.

## Monthly performance distinctions

Performance remains monthly and separated by exact strategy identity/version and batch. No accumulated-period return or pooled strategy/batch return is introduced. Monthly percentages use month-opening equity and New York source-session dates. Partial months must be labeled. Realised results and marked-to-market valuation remain explicitly separate. Published immutable totals are retained for reconciliation; this redesign does not recalculate or rewrite them.

Account open P/L, a holding's unrealized P/L or cost-basis percentage, market value, exposure and buying power are current account measurements. They are not monthly strategy returns and must not be relabeled as such. Missing valuation inputs remain unavailable.

## Verification

Tier: component UI checkpoint. This is not a deployable release or strategy/trading promotion. It grants no additional paper or live execution authority.

| Check | Result |
| --- | --- |
| Affected web build | Passed with 0 warnings and 0 errors |
| Focused backend contracts: AccountPositionsTests, OrderWorkflowIntegrityTests, OrderTicketPlanApiTests, OrderReplacementReviewTests | 36 passed |
| Changed JavaScript and edited UI spec syntax | Passed node --check |
| Independent engineering/design review | Findings addressed; final review reported no issues |
| Initial full browser suite | 166 of 174 passed; follow-up corrections described below |
| Second full browser suite | 172 of 174 passed; remaining failures identified as disclosure assertion formatting and Audit tablet overflow |
| Full browser suite before final Orders rail fix | 175 of 175 passed in 2.4 minutes |
| Orders rail correction and affected browser rerun | 46 of 46 passed in 44.5 seconds after the CSS change, including real-wheel scrolling and hit-target checks at 1440 px and 412 px |

Commands from the repository root:

```powershell
dotnet build src/TradingFlow.Web/TradingFlow.Web.csproj --no-restore -v quiet
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter 'FullyQualifiedName~AccountPositionsTests|FullyQualifiedName~OrderWorkflowIntegrityTests|FullyQualifiedName~OrderTicketPlanApiTests|FullyQualifiedName~OrderReplacementReviewTests' -v quiet
```

From tests/ui, with a fresh GUID in TRADINGFLOW_UI_OWNED_RUN_ID:

```powershell
node node_modules/@playwright/test/cli.js test --reporter=line
```

The changed JavaScript and edited spec files also passed node --check.

The second browser run exposed an assertion that depended on the serialization of the HTML boolean open attribute. The test now reads the disclosure's open property, covering both open="open" and the equivalent empty attribute. Audit tablet overflow was also corrected before the final rerun.

The initial browser failures led to corrections for research/backtest grid sizing, 44 px interaction targets and the distinction between an unavailable first account read and stale previously observed data. The earnings reload test still requested the RVOL filter removed in pre-existing commit 5a6b138. It now checks the supported market-cap filter, restored disclosure and URL state across reload; no removed or nonfunctional filter was reintroduced.

API and safety checks retain the existing account-position, order review/preview/confirmation, replacement and authorization contracts. Browser assertions preserve result and breakout distinctions, advisory authority, current account P/L labels, filter query state and legacy routes. Desktop/mobile evidence is captured by existing populated fixture tests in earnings-workspace-1440.png, earnings-workspace-390.png, news-workspace-1440.png and news-workspace-390.png through Playwright test output paths. Final administrator ticket screenshots were inspected after the rail correction: lower fields remain reachable without clipping.

Root inspected actual screenshots of Desk at 1600 px and 412 px plus dark appearance; Trading, News and Earnings at 1440 px; and Backtests and Strategy runs at 1440 px. That review found two final Backtests presentation defects: the semantic version needed explicit Razor interpolation using v@(...), and the required Test window and Portfolio groups needed to appear together before the full-width optional settings. Both were corrected before the final run. A regression check covers this layout, increasing the browser suite from 174 to 175 tests.

After the 175-test pass, a final administrator Trading screenshot exposed clipped lower order-ticket fields: an inherited maximum height combined with overflow hidden on the Orders rail. The correction restores natural document scrolling. Two new regressions use real-wheel scrolling and hit-target checks at 1440 px and 412 px. The affected orders-screen, trading-workspace, integration-ui-api and responsive-baseline rerun passed all 46 tests in 44.5 seconds after that correction. Root inspected the final full administrator ticket screenshot and confirmed lower fields are reachable without clipping. The complete current suite contains 177 tests; 175 passed in the full run before the isolated rail change, followed by the 46 affected checks including both new tests. No 177-test full rerun is claimed.

Final affected rerun from tests/ui after the Orders rail correction:

```powershell
node node_modules/@playwright/test/cli.js test specs/orders-screen.spec.js specs/trading-workspace.spec.js specs/integration-ui-api.spec.js specs/responsive-baseline.spec.js --reporter=line
```


Browser tests own isolated temporary databases, caches and run state. Market-feed collection is disabled, provider credentials are blanked and fixture mutations do not target an operator or review URL. These checks do not test real provider delivery, original provider receipt timing, broker market connectivity or live orders. No live order was placed. Owned test processes and disposable artifacts are cleaned separately from preserved operator/research data.
