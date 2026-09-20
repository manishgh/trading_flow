# Strategy Flow Phase 5 Order-Intent Checkpoint

**Status:** complete checkpoint; Phase 5 remains in progress

**Date:** 2026-08-31

This historical checkpoint is superseded by the later
[Phase 5 Durable Execution Checkpoint](strategy-flow-phase-5-durable-execution-checkpoint.md)
for current implementation status. Its results below remain the evidence for the
earlier candidate/order-intent slice.

## Scope

This checkpoint closes two lifecycle consistency gaps without claiming the full
Phase 5 execution boundary:

1. An exact, current `Triggered` strategy candidate is consumed in the same SQLite
   transaction that creates its immutable order intent and initial `INTENT` event.
2. A rejected entry-gate prefix is persisted in the same transaction as the exact
   candidate's terminal `RiskBlocked` or `Expired` transition.

## Candidate And Intent Invariants

- Strategy, operator, and protective intents use the typed `OrderIntentKind` contract.
- Strategy intents require candidate ID, Triggered version, and semantic decision
  SHA-256. Operator/protective intents cannot carry candidate authorization fields.
- Candidate consumption is one SQL compare-and-swap over run, state, version, symbol,
  strategy, semantic hash, expiry, revalidation time, and persisted Triggered
  transition evidence.
- The consumption transition records hashes of the Triggered evidence, request, and
  repository-owned consumption evidence. Exact retries verify all of them.
- A filtered unique database index permits at most one non-null candidate ID in
  `order_intents`; an optional restrictive foreign key binds it to `candidates`.
- Intent, initial event, candidate mutation, and consumption transition use one server
  UTC timestamp from `TimeProvider`.
- If the exact Triggered candidate has expired by the repository's server clock, the
  reservation transaction commits `Triggered -> Expired` and its evidence without
  creating an intent or initial order event.
- The authoritative provider session is queried with the server clock inside the
  ordered gate chain. Its trade date and extended-session classification are passed
  to intent creation; caller-supplied timestamps cannot select the trading session.
- Strategy execution authorization is resolved inside the candidate-state gate. A
  revoked or suspended artifact therefore produces an audited terminal gate outcome
  rather than leaving a Triggered candidate behind.
- An existing acknowledged intent can be adopted after its run ends. A stopped run
  cannot begin new broker submission from an existing `INTENT`; `SUBMITTED` remains
  reconciliation-only.
- SQLite immediate transactions serialize candidate consumption and per-session order
  sequence allocation across repository instances.
- Protective-stop repairs derive one deterministic owner intent per position
  generation, symbol, side, and replacement revision. A nonterminal owner is always
  reused when it is absent from a possibly stale broker snapshot. Visible active
  coverage may advance to a supplemental revision for only the remaining deficit.
  Canceled/rejected owners may advance deterministically. A filled owner may advance
  only after fresh broker queries confirm both the fill and the exact remaining
  position; mismatched positions retain protective handling as required by EXE-08/09.

## Gate Outcome Invariants

- The full evaluated gate prefix and a terminal candidate transition share one
  immediate SQLite transaction.
- An exact unexpired Triggered candidate becomes `RiskBlocked`; an exact expired
  candidate becomes `Expired`.
- Missing, stale, mismatched, or operator-override candidate identities are audited
  without mutating a strategy candidate.
- Replaying the same rejection never creates a second terminal transition.
- Missing Triggered transition evidence fails the transaction and leaves both gate
  audit and candidate state unchanged.

## Verification

```powershell
dotnet test TradingFlow.sln --no-restore
dotnet build src\TradingFlow.Mobile\TradingFlow.Mobile.csproj -f net10.0-android --no-restore
git diff --check
```

Results on 2026-08-31:

- Complete .NET suite: 1,356 passed, 0 failed, 0 skipped.
- Focused durability, gate, submission, reconciliation, migration, and lifecycle
  suite: 104 passed,
  0 failed, 0 skipped.
- Android `net10.0-android` build: succeeded, 0 warnings, 0 errors.
- Diff whitespace validation: passed.

## Explicitly Not Claimed

The following Phase 5 requirements remain open and block a final production execution
claim:

- atomic portfolio buying-power, heat, gross-exposure, and position-slot reservation;
- idempotent reservation release after terminal non-fill outcomes;
- a durable recoverable dispatcher and complete accepted-response-lost policy;
- full parent position/fill ownership on protective intents;
- the deterministic shared execution simulator with partial/no-fill, spread,
  slippage, fees, impact, participation, scale-out, and remaining-lot protection;
- full partial-fill protection, takeover, graceful-shutdown, and orphan-adoption
  acceptance matrix.
