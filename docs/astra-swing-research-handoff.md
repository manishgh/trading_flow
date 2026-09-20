# Astra Handoff: Swing-First Research And Backtest Flow

Updated: 2026-09-07

## Mandate

Act as TradingFlow's principal quantitative architect and senior .NET engineer.
Improve the software into an organized, reproducible swing-research and paper-trading
system capable of discovering whether a real after-cost edge exists. Do not promise
profitability, optimize to remembered winners, or convert a pleasing backtest into a
paper strategy without the promotion evidence below.

Swing is the only supported research and execution horizon. Sub-daily infrastructure
is retained solely for swing confirmation and execution timing.
Market Predictor may provide versioned prediction evidence, but it never owns broker
orders, portfolio risk, execution, or promotion in TradingFlow.

## Repository State

- Repository: `C:\project\trading_flow`
- Branch: `main`
- Last committed revision observed: `a74c689 Complete atomic order intent checkpoint`
- The worktree contains a large, intentional, uncommitted phases 6-8 implementation.
  Do not reset, overwrite, or discard it.
- No commit or push is authorized by this handoff.
- Read `AGENTS.md` before changing anything.
- Stop only task-owned TradingFlow/test processes before edits and again after checks.
- Do not modify strategy parameters while repairing infrastructure.

## Read First

1. `docs/remaining-work-checkpoints.md`
2. `docs/verification/audit-streaming-checkpoint.md`
3. `docs/research/non-ml-strategy-research-program.md`
4. `docs/research/non-ml-strategy-research-implementation-status.md`
5. `docs/research/evidence-repository-design.md`
6. `docs/research/research-operations-runbook.md`
7. `docs/backtesting-bias-controls.md`
8. `docs/operating-boundaries.md`
9. `configs/strategy-catalog.json`

For ML context, read but do not edit without separately entering that repository:

1. `C:\project\market-predictor\AGENTS.md`
2. `C:\project\market-predictor\docs\active_edge_rebuild_plan.md`
3. `C:\project\market-predictor\docs\reviews\active_edge_rebuild_handoff.md`
4. `C:\project\market-predictor\docs\reviews\feature_engineering_audit_20260801.md`

## Non-Negotiable Trading Semantics

- One shared decision, risk, execution, and audit brain across backtest, paper, live,
  Web, and Android. Clients display and invoke APIs; they do not recalculate rules.
- Signals use completed bars only. Orders execute no earlier than the next eligible
  bar. Stops use only evidence known before fill.
- Market/news availability is point-in-time. Historical revised news uses the
  conservative provider update timestamp; it cannot be moved back to story creation.
- Use adjusted bars for research ranks and as-traded bars/quotes for execution.
- Model spread, slippage, participation, fees, partial fills, no-fills, and turnover.
- Preserve a fixed account-risk budget. Prediction confidence cannot loosen risk.
- No current-universe snapshot may be projected backward and called promotable.
- No inspected interval may later be relabeled as an untouched holdout.
- A missing value is unavailable, never zero or a pass.
- Strategy YAML remains immutable/versioned research input. Only promoted artifacts
  may enter paper shadow, and promotion remains an explicit human action.

## Current Engineering Checkpoint

Checkpoint 1 in `docs/remaining-work-checkpoints.md` is still open.

Implemented and verified locally:

- streaming atomic artifact publication;
- durable candidate transition journals plus bounded SQLite state indexes;
- candidate-hypothesis and unified-portfolio execution evidence separated;
- durable candidate GUID propagated into hypotheses, portfolio events, and trades;
- manifest commit marker with SHA-256, byte length, record count, relative confined
  paths, publication status, and work-coverage status;
- focused suite: 64 passed;
- 10,000-event resource drill: 2,118,890 disk bytes, 394,408 retained managed bytes,
  one retained preview event.

Full solution result:

- 1,516 passed;
- one live Alpaca pipeline integration first failed because the test process was
  denied network socket access, then reached the host with network permission but
  Windows TLS failed with `No credentials are available in the security package`;
- this is an environment/TLS failure before an HTTP response, not a demonstrated
  product assertion failure. Resolve the host TLS context and rerun before claiming
  full green.

Independent review found no critical issue and these unresolved high findings:

1. End-of-data partial entry/exit executions can disappear from portfolio accounting;
   execution failures are not included in manifest coverage.
2. Fixed buy/sell commissions are added after simulator events, so checksummed
   portfolio evidence cannot reconstruct reported trade fees.
3. Candidate-hypothesis logging can emit a simulated fill before discovering that no
   representable exit lifecycle exists; the short end-of-data path omits its close event.

Medium findings that matter before closure:

- semantically reconcile candidate decision IDs with hypothesis/portfolio artifacts;
- require exactly one core artifact per required kind;
- reject missing candidate IDs instead of generating an unpropagated fallback;
- bound the complete serialized UTF-8 execution record, including evidence JSON;
- enforce/document a hard replay-size ceiling until the all-bars replay is streamed;
- remove stale source candidate-journal paths from returned results after archival.

Do not mark checkpoint 1 complete until these are fixed, counterexample tests pass,
the independent reviewer is rerun, and all task-owned processes are closed.

## Existing Research Evidence

The immutable evidence catalog is separate from operational paper/live storage:

- catalog: `data/research/evidence/catalog.db`
- objects: `data/research/evidence/objects/`
- adjusted six-year daily bars dataset:
  `1a82f7a174d9b5b9a72d02a3ed238e24bf63c1a9f938f2f101b1c0368836a95a`
- raw six-year daily bars dataset:
  `4c64597b38b8ce844eb05626dfa24ee0148c0a97ba371bb7a3942a51d0cd7340`
- provider-updated historical news:
  `ebd79d6c5e9f9a448a1332b206676b809f7e222d86355a5e0f3272d90b8452c9`
- XNYS session evidence:
  `5e055c6fe25ba44bc7d75dfd2d724010d2893826d5632b365a27c2a767c94e2f`

Verify every identifier against the catalog before use. The catalog is authoritative.

The existing 75-stock six-year momentum study is diagnostic only. It projected a
current Finviz survivor set backward, has only 52 monthly formations, and its holdout
has already been inspected. Its encouraging headline statistics are not promotion
evidence. Do not tune against or reopen that holdout.

## Swing-First Work Program

### Gate 0: Make backtest evidence trustworthy

Close checkpoint 1 defects above. Then close checkpoint 2 durable/cancellable run
execution and checkpoint 3 point-in-time universe/candidate identity. Strategy and ML
work may be designed in parallel, but no new result is authoritative before these gates.

### Gate 1: Establish the economic baseline

Run one preregistered cross-sectional momentum benchmark:

`momentum_12_1 = adjusted_close[t-21] / adjusted_close[t-252] - 1`

- point-in-time liquid US common-share universe;
- monthly completed-bar formation;
- top 30 percent, fixed slots, equal weight initially;
- next regular-session open execution;
- 20 completed-session holding/rebalance horizon;
- failed/missing additive gate leaves cash and never backfills from the next rank;
- compare SPY, turnover-aware equal-weight universe, bottom 30 percent, sector-neutral
  spread, and momentum deciles.

Do not add VCP, catalyst, RSI, MACD, discretionary exits, or ML to this first run.

### Gate 2: Correct evidence acquisition

Acquire or prospectively build timestamped historical universe membership including
later listings, delistings, security identity, sector membership, corporate actions,
and terminal returns. If point-in-time membership cannot be obtained, keep results
diagnostic. Finviz current screens are discovery inputs, not historical membership.

Use data from the real provider-supported boundary in or after 2016. The 2016 boundary
does not require a company to have existed in 2016; later listings become eligible
after their own completed-bar warm-up.

### Gate 3: Freeze statistical protocol

- register hypothesis, evidence IDs, formula, universe, costs, partitions, embargo,
  primary metric, rejection rules, and family trial count before reading outcomes;
- chronological development, validation, and one-use holdout;
- at least 60 development, 24 validation, and 24 prospective holdout formations;
- cluster swing inference by formation date;
- HAC/Newey-West plus formation-date block bootstrap;
- report gross/net/excess return, Sharpe, drawdown, turnover, time under water,
  win rate, payoff, expectancy, MFE/MAE, and ticker/sector/month concentration;
- run 1x, 2x, and 3x cost stress;
- reject results dominated by one ticker, sector, month, or regime.

### Gate 4: Add one hypothesis at a time

Only after the baseline passes validation:

1. stock trend: `close > SMA200 AND SMA50 > SMA200`;
2. frozen VCP timing as specified in the binding research program;
3. narrow, independently validated catalyst category using point-in-time availability;
4. ML evidence as an isolated paired addition.

Rank the baseline once. A failed addition leaves the original slot in cash. Never
rerank survivors, because that changes the portfolio and invalidates attribution.

### Gate 5: Shared-engine execution replay

Convert a research effect into an execution hypothesis only after its event/portfolio
study passes. Replay it through the same completed-bar planner, account-risk budget,
portfolio constraints, deterministic execution simulator, costs, partial fills, and
audit manifests used by paper. Compare the result with the research return series and
explain every material difference.

### Gate 6: Paper shadow

Require explicit human promotion of an immutable strategy/config/evidence hash.
Shadow for a frozen window without parameter changes. Reconcile expected candidates,
broker submissions, fills, costs, exits, and missed events. Scale is not authorized
by a good backtest alone.

## ML Integration Contract

`market-predictor` owns feature construction, labels, training, validation, model
artifacts, predictions, and model governance. `trading_flow` owns candidate
admission, portfolio/risk, orders, broker state, and audit.

The existing read adapter is
`src/TradingFlow.Web/Services/MarketPredictorHttpClient.cs`. Treat it as display/
research evidence until the paired experiment below passes.

For swing, ML must be evaluated as an isolated addition to frozen momentum slots:

- input is a versioned prediction with observed-at UTC, horizon, model ID, feature
  contract hash, training cutoff, calibration/readiness, and unavailable reason;
- the prediction timestamp must precede the formation decision;
- training data may not overlap the one-use TradingFlow holdout;
- missing/stale/invalid predictions abstain and never silently pass;
- first compare baseline slots versus baseline-plus-ML score/veto with all other
  holdings, dates, costs, and cash treatment fixed;
- no ML output may increase risk, bypass gates, submit orders, or promote itself;
- preserve both the model evidence and TradingFlow candidate/decision IDs in the
  audit chain.

Before coding cross-project integration, freeze a versioned JSON contract and create
consumer/provider contract fixtures in both repositories. Do not force the projects
to share candle files or internal feature schemas.

## Success Definition

The objective is not “make the backtest profitable.” A credible swing candidate must:

- show positive after-cost excess return in development, validation, and untouched
  holdout;
- retain an economically coherent rank relationship;
- survive cost and concentration stress;
- have sufficient independent formations and securities;
- not rely on one lucky ticker/month;
- remain reproducible from immutable manifests;
- reconcile through the shared execution engine;
- pass a frozen paper-shadow period before any live consideration.

A failed hypothesis is a valid result. Record it, retire the family when required,
and do not tune on the holdout.

## Exact First Actions For Astra

1. Inspect the dirty diff and the three high checkpoint-1 defects; write counterexample
   tests before fixes.
2. Repair execution truth and manifest semantic reconciliation without changing
   strategy rules.
3. Rerun the 64-test audit suite and the full solution suite with approved network
   access for the Alpaca integration test.
4. Obtain one independent code/design review and close the reviewer.
5. Update `docs/remaining-work-checkpoints.md` and
   `docs/verification/audit-streaming-checkpoint.md` with factual evidence.
6. Continue checkpoints 2 and 3 before treating new swing/ML results as authoritative.
7. Produce a preregistered swing experiment request and an evidence-gap report; do
   not run a new holdout until the point-in-time universe and untouched dates exist.

## Copyable Astra Prompt

Read `C:\project\trading_flow\AGENTS.md` and
`C:\project\trading_flow\docs\astra-swing-research-handoff.md` first. Work in
the existing dirty `main` worktree without resetting or discarding user changes.
Act as principal quantitative architect and senior .NET engineer. Swing is the first
research priority, but begin by closing the documented checkpoint-1 execution/audit
truth defects with counterexample tests and independent review. Then complete durable
run execution and point-in-time universe/candidate contracts before running a new
authoritative swing study. Establish the frozen 12-1 cross-sectional momentum
benchmark before adding trend, VCP, catalysts, or Market Predictor evidence, one
paired addition at a time. Keep ML advisory and versioned; it cannot own risk,
execution, or promotion. Use immutable point-in-time evidence, completed bars,
next-bar fills, realistic costs, chronological partitions, an untouched holdout,
and concentration/statistical audits. Explain trade concepts in plain language.
Never promise profit, assume missing evidence, tune on the holdout, or claim an
unrun check. Do not commit or push unless the user explicitly authorizes it.
