# TradingFlow project-wide design review — 19 October 2026

**Source review dated 19 October. The [21 October implementation plan](07-implementation-plan-2026-10-21.md) was approved and records the implemented/offline-verified work and remaining phone/cloud/research checks. The subsequent [prepared-candle reuse design](08-prepared-candle-reuse-2026-10-21.md) was separately approved and implemented on 21 October; its checklist links the completed offline component verification and remaining scope limits.**

This is a separate, one-time review of the current checkout. It supplements, rather than replaces, [architecture.md](../../architecture.md) and [architecture-and-review.md](../../architecture-and-review.md). Existing documents describe intentions and earlier checkpoints; the linked source and the component reviews establish what this review actually observed.

Reviewed checkout: `research/swing-pit-review-20261008`, base commit `ecec7f8`, including existing modified and untracked source. This is not a claim about the running app, another checkout or an Azure deployment. Earlier published studies remain unchanged.

## Start here

| Document | What you can review |
|---|---|
| [07 — Implementation plan, 21 October](07-implementation-plan-2026-10-21.md) | Current decisions, delivery order for all nine packages, acceptance checks, estimates and actual completion status |
| [08 — Reuse prepared candles, 21 October](08-prepared-candle-reuse-2026-10-21.md) | Approved reuse of immutable candle/calendar versions and one fixed input selection per comparison; API/Web/Android evidence and acceptance checks |
| [01 — System map, flows and plain names](01-system-map-and-flows.md) | Every project, what it does, how a screen reaches the server, how market data reaches a decision, and where writes happen |
| [05 — Decisions and proposed work](05-decisions-and-proposed-work.md) | Confirmed requirements versus changes you can approve, reject or revise |
| [06 — Hardcoding, configuration and rule audit](06-configuration-and-rules-audit.md) | Settings and rules to keep, consolidate, move out of executable code, or consider removing; changes introduced in this work are identified separately |
| [02 — Trading and research](02-trading-and-research.md) | Strategy evaluation, risk, execution, portfolios, research, optimization, command-line tools and sentiment inference |
| [03 — Application, Web and Mobile](03-application-web-and-mobile.md) | Screens, server calls, job handling, displayed results, contracts, settings and application ownership |
| [04 — Data, platform and security](04-data-platform-and-security.md) | Providers, databases, warm-up, archives, authentication, deployment, credentials and test isolation |

Read 01 and 05 first. The longer component reviews provide the source evidence behind each proposal. Stable finding IDs (`TR`, `AP`, `DS`) let you approve or discuss an individual issue without approving an entire rewrite.

Priorities are review work priorities, not vulnerability scores: P1 means address before relying on the affected display, operation or deployment; P2 means address before extending the affected design; P3 is lower-priority cleanup. A confirmed source defect is distinct from a missing requested capability or a suggested refactor.

## What was reviewed

Three senior architect personas divided the project: trading/research architecture; application/client architecture; and data/platform/security architecture. The trading and platform reviewers independently checked one another's major findings. The parent reviewer reconciles terminology, project coverage and cross-component decisions.

The inventory covers all 18 `TradingFlow.*` projects found under `src`, the two source-controlled research tools, `DbChecker`, local database helpers, the FinBERT Python sidecar, UI tests, build/CI and deployment definitions. Component documents list internal areas, inputs, outputs, side effects, settings and ownership. The separate Market Predictor/ML repository was not present in this checkout and was not reviewed.

This is **whole-project design coverage through source inventory and traced representative workflows**, not an assertion that every line received a security audit. Generated files, dependencies, screenshots, secrets, private database contents and large captured datasets were not inspected. Runtime behavior, cloud permissions, actual images and third-party responses were not tested. No build, test, backtest, provider call, broker action, deployment or runtime change was made for this review.

Document checks: 285 local links/source-line bounds, code fences and whitespace checked; 12 Mermaid flow diagrams included. Each component review received independent source-based cross-review; glossary and attribution corrections were applied. Diagram rendering was not exercised in a browser, and document checks do not certify runtime behavior.

## Main findings to discuss

- **Deployment and access:** templates propose shared network storage for SQLite; warm-up mutation endpoints lack caller authentication; Docker build contexts do not exclude all local secret/data files; Mobile permits password submission over HTTP. These are conditional source-level findings, not claims that a deployment was exploited. See DS-01–04.
- **Reusable workflows:** cohort/strategy execution should come from one saved plan instead of code containing a particular study's dates, stock groups and counts. Several application/provider workflows have more than one owner. See TR-01, TR-02, TR-08 and the application review.
- **Reliable presentation:** what the browser/phone receives must support monthly strategy/cohort results and show account positions even when no run is active. Display wording must distinguish available data, missing data and application failures. See the application review.
- **Complexity and cost:** separate order protocol validation from mutation and publication, avoid repeated work over unchanged bar history, and validate optimization budgets before generating combinations. These are source-level cost concerns; no benchmark was run. See TR-03, TR-04 and TR-06.
- **Configuration and rules:** there should be fewer meaningful settings with explicit owners, rather than separate defaults, fallback rules and study constants across UI, CLI and services. See 06.

## Confirmed requirements

Each cohort/group has its **own portfolio and configurable starting capital**. Each strategy test has independent capital. Five stocks and $10,000 are examples. Shared market-data reading must not create shared money or pooled strategy/cohort returns.

On 21 October the user selected configurable paper/live defaults of five new stocks per day and five open positions. Historical research includes all selected stocks and does not inherit the operational daily-entry cap. The approved counting/account rule and its implementation evidence are recorded in 07. Automatic and saved/manual cohorts are supported; each strategy/cohort keeps independent capital.

The Desk must show the selected strategy's actual checks and reasons for each stock, replacing its separate generic rules. Use the existing strategy terms rather than another score or invented name.

Before additional implementation, present its concrete design and obtain user approval. Plan 07 records approval of the earlier remedies; document 08 records the separately approved candle-reuse change and its verification status. Real provider-backed research execution needs its separate concrete research approval. Offline deterministic workflow tests have run, as recorded in the verification documents.

## How to respond

You can say, for example: “Approve D-03 with these screen labels; revise D-04 to allow three new symbols per New York trading day; defer deployment changes.” No broad rewrite, new configuration collection or experiment is implied by approving one item.
