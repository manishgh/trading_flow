# Hardcoding, configuration and rule audit

Status: source-based review and removal/consolidation proposals. No setting, strategy, safety rule or default was changed. The user asked for no hardcoding and an audit of unnecessary settings/rules, including those introduced by the agent. Detailed component inventories are in [02](02-trading-and-research.md), [03](03-application-web-and-mobile.md) and [04](04-data-platform-and-security.md).

## What the audit is deciding

The goal is understandable behavior with an explicit owner and one effective source. Moving every number into another configuration file would preserve complexity. The following classification is a proposal for user review, not a new collection of runtime switches.

| Classification | Meaning | Proposed treatment |
|---|---|---|
| User or strategy choice | Capital, stock membership, dates, stops/targets, approved trade limits and execution assumptions | Use existing typed strategy/run settings where possible; preview and save exact values |
| Saved experiment fact | Exact strategy identities, input files/fingerprints, group membership, approved scenario, work budget and reviewed period | Store once in the saved plan; derive run/report counts and labels from it |
| Environment setting | Provider profile, service origin, data roots, identity/secrets source and worker resources | Host/deployment owns it; validate it centrally; do not bury machine paths in helpers |
| Fixed mathematical/protocol meaning | Nanosecond conversion, record format, recognized provider code definitions | Name and explain it; do not create an arbitrary UI setting that changes the unit or protocol |
| Safety or evidence invariant | Do not use future information, authorize account/order operations, keep fingerprints and completed-data rules, preserve protection/exits | Keep enforcement; document the invariant and reject unsupported requests rather than exposing a bypass |
| Duplicate or unexplained rule | Repeated defaults, silently ignored fields, separate advisory thresholds, fallbacks with no clear owner | Consolidate or propose removal; first identify every consumer and resulting behavior |

Configuration must not claim a feature is supported just because a field accepts a value. For example, changing a latency number does not implement delayed-fill accounting or make it broker-calibrated.

## Changes attributable to this execution-path work

The following additions and driver changes belong to the preceding execution-path work. Some are new files; others add fields or behavior to existing files, and do not imply whole-file authorship. They remain unapproved for further extension. Existing code is preserved while this design is reviewed; this document does not delete published artifacts or start another study.

| Current item and source | Why it exists now | Proposed disposition and reuse |
|---|---|---|
| Dated sibling data folders, parent fingerprint, four recipe keys and five cohorts in [PortfolioEventRunner](../../../tools/research/PortfolioEventRunner/Program.cs#L22) | Bind one existing historical study | MOVE TO SAVED PLAN. Generic runner accepts explicit evidence roots and exact strategies/groups; preserve the original study as one immutable plan instance |
| Fixed 20 passes, $10,000, 1% risk, 25% notional and four positions in the same driver | Reproduce that study's limits | MOVE TO SAVED PLAN / REUSE PortfolioConfig. These are not universal user preferences; derive work count from membership and strategy choices |
| Fixed 140 report rows, dates, starting balance and cost-name suffixes in [MonthlyEventReport](../../../tools/research/PortfolioEventRunner/MonthlyEventReport.cs#L32) | Check the original 20-run/seven-month matrix | REMOVE duplicated executable expectations. Derive months, balances, costs and expected rows from the same approved plan/results; keep validation against that source |
| Fixed five-minute entry window in [EventPortfolioExecution](../../../src/TradingFlow.Backtesting/EventPortfolioExecution.cs#L69) | Define exclusive expiry for the first event scenario | MOVE TO versioned execution assumptions if configurable expiry is wanted; reuse original route-expiry enforcement. Do not silently extend an already committed order |
| Zero source/report delay, native full-parent protection, actual-quote spread and one-strategy checks in [controller](../../../src/TradingFlow.Backtesting/EventPortfolioExecution.cs#L100) | Refuse scenarios the implementation cannot simulate | KEEP capability validation until another scenario is explicitly designed and verified. Save supported assumptions in the plan; do not turn checks into bypass flags |
| Condition contract, modifier classification and 30-second quote-age assumption in [source adapter](../../../src/TradingFlow.Alpaca/SipPortfolioExecutionSource.cs) / driver | Bind which prints/quotes the diagnostic model accepts | SAVE model/version/source metadata once. Review whether a broader broker condition model is required. Quote age is a declared assumption, not a measured provider guarantee |
| `NativeParentCommitted` in [event command contract](../../../src/TradingFlow.Engine/Risk/ExecutionEventEvidence.cs#L18) | Distinguish precommitted broker-native children from client-managed protection | KEEP an explicit workflow distinction; review a clearer typed command/contract design before adding more booleans. It is not an operator override |
| `EvidenceUnavailable` in [execution failure](../../../src/TradingFlow.Domain/Backtesting/BacktestResult.cs#L78) | Represent missing execution evidence even before an order fills | KEEP the distinction from open exposure. Review a clear failure-kind contract before expanding flags; never reinterpret missing data as zero return |
| `EventExecutionEndUtc` in [candidate replay contract](../../../src/TradingFlow.Domain/Backtesting/ExecutionReplayPolicy.cs) | Prevent unresolved orders/positions from filling in a later separate archive window | KEEP a frozen execution boundary; derive it from the approved calendar/plan, not an unrelated current date |
| Event source binding plus fake not-executed failures and regeneration/ID remapping in the driver | Capture inputs before replay and use ordinary publication afterward | CONSOLIDATE into explicit prepare → execute → publish over one frozen snapshot. Reuse the opportunity snapshot/observer and artifact checks; remove the lifecycle workaround after parity verification |
| Hardcoded local credential file, calendar dates and HTTP composition in [AlpacaMetadataCapture](../../../tools/research/AlpacaMetadataCapture/Program.cs#L6) | Quick one-time provider metadata/calendar collection | REMOVE machine dependency / CONSOLIDATE provider ownership. Use existing host configuration, endpoint resolver and explicit saved capture range. Do not make raw credential-bearing URL overrides |
| Repeated local result/failure lookup by candidate ID | Connect portfolio records to audit output | REUSE indexed identity maps instead of repeated scans. Preserve exactly the same decisions and journal evidence |

No new mathematical strategy indicators or parameter optimization were authorized by these changes. Their structural problems should not be addressed by adding new trading rules.

## Wider project items: authorship is not established by this review

The checkout already contains many modified and untracked files from earlier work. A file being modified does not prove this agent introduced all of its settings, or that earlier user approval was absent. The component reviews report current source facts and label approval history unknown where appropriate.

| Item | Problem to review | Proposed disposition |
|---|---|---|
| Browser/phone/CLI defaults for history, capital, risk, slots and connection origins | Duplicated values can disagree with the server's effective run settings | CONSOLIDATE authoritative defaults; display the resolved value; allow only meaningful approved overrides |
| UI run writer's hand-built engine configuration; [writer:64](../../../src/TradingFlow.Web/Services/RunConfigWriter.cs#L64) | The written engine block omits existing `ExecutionReplayPolicyId`, `PrecommittedLiquidationSourceUtc` and `AllowLateWarmup` fields; it also forces a minimum timeout of 1800 seconds | REUSE typed configuration with explicit supported overrides and round-trip verification; reject unsupported modes explicitly and review the unexplained timeout floor |
| Full strategy trades removed by [UI job projection:311](../../../src/TradingFlow.Web/Services/BacktestJobService.cs#L311), but required by monthly calculation | The producer/consumer pair cannot calculate monthly rows for nonempty accepted-trade results | CONSOLIDATE monthly publication before trimming or load a compact immutable monthly result; do not add a flag to bypass reconciliation. See AP-05 |
| Wishlist-watch RVOL, distance, ATR/EMA/MACD rules | Separate advisory logic adds another definition of an interesting stock | USER DECISION: deliberately keep a named advisory profile, reuse selected strategy evidence, or retire the separate score. Approval/authorship history is not established |
| Desk's built-in stock screens and fallback filter recipes | A code default can look like an operator-created or saved provider list | Review source ownership and naming; use approved saved profiles or remove unused recipes, without altering the user's lists |
| Optimizer's unknown-metric fallback and materialized combination product | Invalid requests can receive an unintended objective; budgets may be checked after expensive enumeration | REMOVE silent fallback; validate exact supported objective and bound planned work before generation |
| Warm-up parallelism, timeouts, lookback, queue bounds and retention across settings/deployment | Multiple owners/fallbacks; a per-ticker limit does not bound accepted queued work | CONSOLIDATE one effective resource policy and validate it at acceptance; keep technical settings off unrelated trading screens |
| Warm-up credential ancestor search and local-file fallback | Host behavior depends on launch directory and another application's file | REMOVE implicit search; one explicit Development/Production credential-source policy |
| Deployment replicas, storage classes, worker stock lists and roots | Configuration may violate actual SQLite writer/storage rules | CONFIGURE deployment-specific choices subject to approved ownership constraints; do not add more replicas to solve an ownership defect |
| Sentiment model name/revision and readiness | A model name does not identify exact downloaded weights or successful load | SAVE exact model revision/provenance and expose accurate readiness; retain only useful provider/model choices |

## Removal and verification process — approval pending

For each proposed removal or consolidation: identify all callers and serialized consumers; state the current behavior; explain the replacement or absence of the rule; obtain user design approval; implement one scoped change; verify affected behavior; show resolved settings and any migration impact. A deprecated-looking field is not removed merely because its name is unclear.

Keep audit/version fields separate from operator tuning. Keep code constants separate from experiment inputs. Avoid settings that are accepted then ignored, automatic fallback policies that change trading meaning, and multiple booleans representing mutually exclusive workflows.

No configuration/removal package has been approved. [05](05-decisions-and-proposed-work.md) provides the user decision list.
