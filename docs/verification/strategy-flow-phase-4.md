# Strategy Flow Phase 4 Verification

**Status:** complete

**Date:** 2026-08-31

## Scope

Phase 4 makes one strategy decision kernel authoritative for backtest, paper/live,
diagnostic preview, and mobile strategy-gated automation. It owns signal generation,
direction, confluence, point-in-time catalyst policy, session policy, admission,
trigger confirmation, initial invalidation, expiry, and the semantic order plan.

## Decision And Persistence Contract

- Strategy identity is the immutable strategy ID, semantic version, resolved content
  hash, and admission profile.
- Candidate identity binds symbol, strategy hash, setup key, discovery window, and
  owning run ID. Parallel runs cannot collide on the same symbol/bar.
- The only state graph is `StrategyCandidateStateMachine`.
- Paper/live persists candidates and append-only transitions in SQLite with optimistic
  version checks. Discovery evidence is immutable after first persistence.
- Backtests use an isolated in-memory implementation of the same repository contract
  and the same `StrategyCandidateDecisionOrchestrator`. Every discovery and decision
  is synchronously flushed to a unique write-through NDJSON recovery journal.
  Completed runs atomically publish the full candidate/transition journal as
  `*.candidate-decisions.json`, separate from compact result retention, then remove
  recovery spools. Cancellation/process failure leaves flushed spools intact.
- The kernel emits one canonical pre-risk order plan containing direction, trigger,
  completed pre-fill stop, target policy/reference, slippage, and expiry. Backtest,
  paper/live, and mobile hand that same plan to account-risk sizing.
- Order admission requires a current `Triggered` candidate and a matching persisted
  trigger transition sequence and semantic decision SHA-256.
- Transactional candidate consumption with order intent/outbox remains Phase 5. It is
  deliberately not represented as complete in Phase 4.

## Catalyst Timing

Alpaca provider publication/update time, first observed receipt, and sentiment
classification completion are recorded separately. The decision-known timestamp is
the later of observed receipt and classifier completion. A revision with a future
decision-known time is invisible to an earlier decision. Historical REST data fetched
after the event therefore cannot masquerade as point-in-time receipt evidence.
Incomplete records are excluded: provider publication/update time cannot substitute
for observed receipt, classification version, or decision-availability evidence.

## Adapter Parity

The semantic SHA-256 excludes adapter-only aggregate IDs, source labels, evidence IDs,
and collection timestamps. Full canonical audit JSON retains those values. Golden
fixtures pass through the real backtest and paper discovery assemblers and their
different persistence implementations, verifying identical semantic hashes, states,
directions, reasons, transition paths, and rule outcomes for:

- regular-session long without catalyst;
- regular-session long with catalyst;
- short research;
- extended-hours long.

Short parity covers strategy decisions and audit. Paper/live short order routing stays
fail-closed until Phase 5 implements its broker, borrow, protection, and reconciliation
invariants.

## Bypass Removal

- LiveRunner and mobile `validate_strategy` route through the orchestrator.
- The former live-only news veto and mobile synthetic signal fallback are removed.
- `operator_direct` requires a typed server-issued authorization, is paper-only, and
  cannot claim strategy selection or candidate identity. Android requires an explicit
  exit-guardian strategy and persists its immutable identity separately.
- Mobile sessions atomically reserve one active owner per ticker before scheduling;
  their lifetime is independent of the initiating HTTP request, and immediate cancel
  is race-tested.
- Market Predictor values have zero qualification, veto, ordering, and execution
  weight. Wishlist setup heuristics are labelled `Observed setup` and are decision
  support only.

## Verification

```powershell
dotnet build src\TradingFlow.Web\TradingFlow.Web.csproj --no-restore
dotnet test TradingFlow.sln --no-restore --verbosity minimal
dotnet build src\TradingFlow.Mobile\TradingFlow.Mobile.csproj -f net10.0-android --no-restore
git diff --check
```

Results on 2026-08-31:

- Web dependency build: succeeded, 0 warnings, 0 errors.
- Complete .NET test suite: 1,330 passed, 0 failed, 0 skipped.
- Focused decision-kernel and real adapter matrix: 19 passed, 0 failed, 0 skipped.
- Focused decision, migration, order, and mobile lifecycle set: 60 passed, 0 failed,
  0 skipped.
- Android `net10.0-android` build: succeeded, 0 warnings, 0 errors.
