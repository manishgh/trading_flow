# Implementation plan — 21 October 2026

Scope: all nine packages in [05](05-decisions-and-proposed-work.md). Deliver working workflows using existing components; do not turn each finding into a separate redesign. Deployment and service-access changes come last.

Approved on 21 October 2026: “Lets go please make sure API and UI integration web and android reflect the changes here.” This authorizes the implementation below, including the described account-wide operational counting rule. Completion boxes require verification evidence; approval alone does not complete them. Device testing and actual deployment remain separate.

## Decisions already made

- [x] Review the existing projects and trace their flows: [system and data flows](01-system-map-and-flows.md), [trading](02-trading-and-research.md), [clients](03-application-web-and-mobile.md), [data and deployment](04-data-platform-and-security.md).
- [x] Obtain independent plan/execution, code-impact and design reviews of the proposed scope. Their estimates below are source-based; they did not run builds or backtests.
- [x] Include P-01 through P-09; prioritize a functioning system over a one-day deadline.
- [x] Support automatic groups and saved/manual cohorts. Show membership before starting.
- [x] Give every strategy/cohort combination its own portfolio and configurable capital.
- [x] Allow settings and cohorts to be saved without starting a run.
- [x] Include all selected stocks in historical research. Twenty-five stocks grouped by five means five groups, not a five-stock study.
- [x] Keep paper/live daily-entry limits separate from historical research. Default paper/live settings: five new stocks per day and five open positions, both configurable.
- [x] Show monthly results separately for each exact strategy and cohort. Do not present pooled or accumulated-period returns.
- [x] For each stock, show the selected strategy's checks, their pass/fail results and reasons. Replace the Desk's separate generic checks; use actual strategy terms, not a new score or invented feature name.
- [x] Update API, Web and Android consumers together when their shared models change. Android device verification can wait for the phone; record that explicitly rather than checking it off.
- [x] No backward-compatibility layers are required. This does not authorize deleting operator data, cached market history or published results.

## One reusable workflow

Reuse the existing selection screens, `BacktestRunConfig`, portfolio configuration, `RunWorkflowService`, durable jobs, backtest runner and result readers. Extend their current contracts rather than adding another scheduler or executor.

The user selects a stock source or saved/manual cohorts, strategies, dates, grouping, capital and existing execution settings. The server resolves membership and exact strategy versions, validates the settings and shows the stocks, independent portfolios and execution count. Save does not enqueue work. Start submits the previewed plan through the existing jobs. Each execution publishes its own monthly records and evidence.

Automatic grouping must use a documented stable symbol order. Keep the final smaller group and show it. Manual cohorts must validate duplicates within a group; intentional overlap between separately named groups must be visible. Research portfolio capacity remains configurable and distinct from group size. Cash, position sizing and strategy/risk rules continue to apply in historical runs.

Keep configuration with its existing owner:

- Strategy definitions own indicator parameters and entry/exit rules; retain exact version/content identity.
- A saved run plan owns resolved stocks, groups, dates, capital and the selected execution settings.
- Shared, centrally stored mode settings own limits: historical research explicitly resolves no operational daily-entry cap and its own research portfolio capacity; paper and live each resolve configurable defaults of five new stocks/day and five open positions. Missing settings must not silently inherit another mode's values. Save the effective settings with the plan. Host settings own credentials, storage and resource budgets; clients display server-resolved values rather than introducing their own defaults.

Do not add a general configuration framework, strategy language, second database or per-study source project. A value needs a setting only when a supported user or operating choice requires it.

## Delivery order and completion checks

Completion below refers to the compiled implementation checkpoint, supported by [integration evidence](../../verification/application-integration-2026-10-21.md). Actual device behavior, container-image/cloud validation, new provider-backed research and deployment remain separate. The latest core-data retention requirement has a verified follow-up below.

### 1. Preserve settings and establish the execution contract — P-02, necessary P-06

- [x] Round-trip supported run settings through API/Web/Android/CLI; reject unsupported settings explicitly. Inspect `RunConfigWriter`, which currently writes selected engine fields and can omit replay/warm-up settings.
- [x] Define explicit runnable strategy/cohort entries using existing run/job contracts. `RunWorkflowService` currently takes `files[0]`; the other returned YAML files include strategy evidence, not additional runnable jobs. Do not execute every returned file.
- [x] Connect the event execution path through reusable prepare, execute and publish operations. Use the same frozen candidates and replay binding throughout; do not depend on the dated research driver's regeneration/remapping workaround.
- [x] Supply an execution-specific frozen `EventPortfolioReplayBinding` through the normal API/job path. Web currently constructs `BacktestRunner` without it. Preserve SIP's one-strategy-per-execution requirement and verify an actual deterministic SIP execution through that path, not just configuration serialization.
- [x] Decide database writer and durable-job ownership before changing persistence. Implement cloud/service-access work in stage 6; avoid a temporary ownership design that stage 6 must replace.

Acceptance: a saved request retains the chosen effective settings; an unsupported mode fails with a specific message; preparation and execution refer to the same strategy, stocks, candidates and execution evidence. Shared decision rules remain in C#; the separate Python model service remains separate.

### 2. Configure, run and view cohort comparisons — P-03, P-04, P-05

- [x] Extend the normal plan/save/preview/start API and screens for automatic and saved/manual cohorts, exact strategies and independent capital. Reuse existing cancellation, leases and recovery.
- [x] Validate total strategy/cohort work against configured resource limits before accepting executable work or expanding/enqueueing child jobs. Preview the execution count and any rejection reason; individually bounded jobs do not make an unlimited plan acceptable.
- [x] Make historical research independent of paper/live daily-entry limits; keep research capacity, operational new-stock counts and operational open positions separate.
- [x] Approved counting rule: count distinct symbols on their first actual entry fill in a New York trading day, once per symbol that day; count a holding until fully closed. The scope is the operational account across its runs, so adding a strategy cannot multiply the limit. Pending entries reserve capacity; partial fills and re-entry of the same symbol do not count it again that day. Implementation and enforcement checks remain part of this stage's acceptance.
- [x] Generate compact monthly results before trimming UI snapshots. Preserve exact strategy/version/content hash, cohort identity, original capital, New York month, month-opening equity, closing equity, valuation basis and partial/unavailable status. Calculate each month's percentage using that month's opening equity, not the original run balance; withhold percentages when the required equity values are unavailable.
- [x] Keep native-daily realized results distinct from event-execution marked-to-market results. Reuse `MonthlyBacktestPresentation` and `MonthlyEventReport` accounting where applicable; do not label their different valuation methods as equivalent.
- [x] Update Web and Android result displays together. Obtain actual account holdings through the account reader even without active jobs; show a failed account read as an error, not an empty account.

Acceptance: preview and execute 50 stocks in groups of ten with four strategies (20 independent portfolios), and 30 in groups of five with two strategies (12). Use deterministic fixtures for these checks, not provider calls or real orders. Also verify leftover groups, saved settings without execution, capital isolation, cancellation/restart, partial months and unavailable valuations. No selected stock disappears because of the operational daily limit.

### 3. Consolidate ownership and finish durable preparation — remaining P-06, P-07

- [x] Consolidate demonstrated duplicate admission, provider composition, credential resolution and request lifecycle paths. Reuse existing interfaces; do not mass-rename working components.
- [x] Persist accepted preparation work through the existing durable-job pattern. Enforce a configured queue/work budget and show rejection, cancellation and retry status.
- [x] Separate cache completion from durable archive commitment. Freeze archive bytes before upload and retain pending/failed status for retry.

Acceptance: fake-provider tests cover restart, duplicate submission, cancellation, queue saturation and upload failure. Retries retain the same inputs and candidate identities; later runs cannot change an earlier artifact's bytes. Test fixtures stay out of operational wishlists and are cleaned after tests. Preserve operator state and published evidence.

### 4. Simplify and measure expensive work — P-08

- [x] Split order validation, state mutation and publication into understandable operations; add purpose comments to changed functions. Preserve decision and event order.
- [x] Reuse unchanged completed-bar calculations. Reject unsupported optimizer metrics and excessive combination counts before enumerating combinations.
- [x] Profile representative event-state copying and receipt retention before choosing further data-structure changes. Record fixture size, elapsed time and peak memory.

Acceptance: deterministic replay tests preserve fills, costs, risk decisions and timestamp handling. Benchmarks demonstrate the claimed improvement; no new fill assumptions or strategy tuning are introduced to accelerate execution.

### 5. Show the selected strategy for each stock; identify the model — P-09

- [x] Replace the Desk's independent `WishlistSwingWatchEvaluator` rules with the selected strategy's shared assessment. Inspect `Desk.cshtml`, `watch-desk.js`, their server endpoints/contracts and corresponding Android consumers together.
- [x] Show strategy name/version, the conditions that it actually uses, observed values, pass/fail or missing-data reasons, and the evidence time. Show stop/target only when that strategy produces them; do not invent values or add another score.
- [x] With no selected strategy, display “Select a strategy.” Missing evidence must identify what is missing. Passing technical conditions is distinct from an order being admitted after cash/risk checks and does not submit an order.
- [x] Pin and report the sentiment model revision actually loaded; report load failure explicitly. Do not treat configuration alone as proof that inference is available.

Acceptance: the same strategy, inputs and evidence time produce the same checks/reasons in Desk, backtest and execution. A strategy without a particular indicator does not acquire that check from the old generic evaluator. Verify changed API and both clients; record device checks separately if Android is unavailable.

### 6. Finish deployment and service access — P-01, last

- [x] Apply the earlier writer/storage decision to deployment definitions; document local SQLite ownership and backup/restore behavior. Do not use shared network storage as an unexamined SQLite solution.
- [x] Protect warm-up mutation endpoints, exclude secrets/data from container build contexts, and align HTTPS and credential-source behavior across services and clients.
- [ ] Verify with disposable configuration, service-authorization tests and container/storage manifest checks. Prepare Azure deployment instructions with prerequisites and costs sourced at that time.
- [ ] Obtain separate authorization before deploying or changing external infrastructure. Completing templates is not an Azure deployment.

## Verification and review throughout

- [x] At each slice: plan/execution reviewer checks requirements and completion evidence; senior architect checks ownership, complexity and reuse; independent code reviewer checks changes and failures. Keep reviews focused and heavyweight builds/tests serial.
- [x] Run focused tests for changed engine/risk/job/persistence behavior, then required project/contract checks. Update API and affected Web/Android calls in the same slice. Do not claim device verification from compilation alone.
- [x] End with one integrated configure/save/preview/start/cancel/recover/results exercise using isolated fixtures. Ensure tests leave no source-list or database contamination.
- [ ] After user approval of a concrete research plan, measure one representative historical execution before forecasting the full matrix. Report monthly results separately by strategy/cohort; no trading or strategy promotion is implied.

The linked [component reviews](README.md) identify source files and findings for each package. Narrow each implementation checkpoint to those affected files; do not rewrite unrelated working modules. An approved design change still requires the existing data-timing, strategy authorization and risk boundaries.

## Time estimate and limits

The three independent static reviews estimated approximately 16–30, 24–40 and 30–50 focused eight-hour engineering days respectively. The spread reflects how much consolidation, client integration and failure testing proves necessary. **Plan for roughly 4–8 working weeks of implementation effort, with a 6–10-week allowance if the identified execution/persistence/client gaps require the larger remedies.** This is not measured agent wall-clock runtime or a delivery-date guarantee. Three reviewer roles do not divide implementation time by three.

The critical path is settings/contracts → reusable execution → monthly results and both clients → integrated checks. Model identification, selected-strategy display and some deployment preparation can proceed independently after shared contracts settle. Stop and revise the estimate when the first complete workflow exposes materially different work; do not add speculative redesign to fill the allowance.

Backtest compute time is separate and currently unmeasured. Execution count is the sum of strategy/cohort combinations across the approved scenarios; elapsed time also depends on dates, feed, event volume, preparation and machine capacity. No full rerun, provider collection or deployment has started under this plan. External setup, human news assessment, phone availability and new historical-data collection are not included in the engineering estimate.

## Review status

The user approved this detailed implementation design and requested API, Web and Android integration on 21 October 2026. Check off implementation only with concrete evidence. Actual provider research runs, strategy promotion, orders and external deployment are not authorized by this implementation approval.

## Latest data-protection requirement

- [x] Record in AGENTS.md that core market/news/operator data cannot be deleted under broad cleanup instructions; challenge a destructive request and obtain fresh dataset-specific confirmation even if the user requested it accidentally.
- [x] Remove eight proven unused source files: three scratch probes and the five-file dated PortfolioEventRunner. Preserve shared datasets and published results/evidence; see [cleanup manifest](../../verification/refactoring-cleanup-2026-10-21.md).
- [x] User approved the provider-download retention correction on 21 October 2026. Originals now stay under configured RawRoot; Alpaca bars/calendar archive before parse; automatic expiry is refused; nested run spools remain disposable. Independent reviews and 138 focused offline tests passed. See the [cleanup manifest](../../verification/refactoring-cleanup-2026-10-21.md). No real provider-backed research run occurred.
- [ ] Verify the affected Android screens on a phone. Compilation and source review do not verify device interaction.
- [ ] Inspect disposable Docker images and validate rendered Kubernetes/Bicep definitions before deployment approval. Source/template tests alone do not complete these checks.
