# Swing preparation implementation verification

Date: 27 September 2026. Tier: component checkpoint under the repository risk-based verification policy.

## Delivered

Configurable nightly preparation and morning review; disabled-first immutable definitions; authoritative calendar planning; bounded durable scheduling/retries; fenced atomic publication; owner and CSRF enforcement; immutable source/evidence history; optional discovery filters and context; auditable manual curation; exact-membership handoff to existing research and paper workflows; shared Web/Android editor, history, review and notifications.

Independent senior requirements and code/design reviewers approved the final scoped implementation after fixes for freshness laundering, cancellation/publication races, optional intersection dependencies, evidence hashing, enabled-first bypass, failure notifications, chained curation, retry identity, scheduler revision races and publication deadlines.

## Verification evidence

- Affected backend regression command: `dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~Preparation|FullyQualifiedName~DatabaseMigrationTests|FullyQualifiedName~UniverseWorkflowIntegrityTests|FullyQualifiedName~DurableJobProcessorTests|FullyQualifiedName~RunWorkflow" -v quiet` — **69 passed, 0 failed, 0 skipped**. Covers calendar/DST/holidays/early close, settings bounds, SQLite uniqueness/concurrency/lease/cancellation, migrations/model drift, owner access, real cookie/CSRF/shared client, source failures/empty/incomplete/capacity, expiry, immutable wishlist protection, no orders/runs, curation inheritance and deadline downgrade consistency.
- Web project builds with **0 warnings, 0 errors**.
- `npx playwright test specs/workflow-integration.spec.js specs/preparation.spec.js` — **11 passed**. Includes existing candidate replay, audit, order-preview retry and backtest command regressions. Desktop 1440px and phone 412px configuration/history/review, retained drafts, captured article links, exact retry command, curation, and snapshot-bound paper navigation with no job creation. Uses real login plus explicit API fixtures for deterministic UI cases.
- `dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj -c Release --no-restore -v quiet` — **0 warnings, 0 errors**. Final Release APK installed on an isolated Android API 35 emulator.
- Native checks: More → Preparation; cold preparation intent → login → exact snapshot; denied notification permission with retained history; completed daily evidence; editable draft retained across navigation; save produces revision 2 while paused; actual Android notification delivery and tap opens the intended labeled fixture revision. The fixture database and emulator are isolated from trading state.
- External GET-only smoke: persisted AAPL/MSFT source, actual Alpaca calendar and daily data, the real preparation service/durable worker/SQLite repository. **Completed / complete**, target session **2026-09-28**, **126 completed SIP daily bars per symbol** through **2026-09-25**. No missing required evidence. Workflow remained disabled; the HTTP handler rejects non-GET requests. No orders were placed.
- `git diff --check` passed.

## Boundaries and deliberately unrun checks

No Market Predictor changes/integration, GitHub sync, strategy promotion or order placement. Secrets, operational database, candle cache and paper state were preserved. No user workflow was enabled. Task-owned acceptance hosts/emulator are stopped at completion.

A naturally timed future morning session and external paper order → fill → protection → exit remain operational acceptance gates. Deterministic tests cover morning semantics; the real provider smoke covers nightly preparation from the latest completed session. The full unrelated test suite was not rerun for this component checkpoint; the prior deferred Market Predictor fixture mismatch is unchanged and was not suppressed. No Python consumer exists for the new TradingFlow-only preparation contract.

Operational notes: current source validity is three minutes; snapshot lifetime cannot extend it. Optional news/earnings coverage remains visibly unavailable where no local evidence exists. Native alerts are foreground/reconnect notifications, not background push. Production migrations are delivered but only isolated databases were migrated during verification.
