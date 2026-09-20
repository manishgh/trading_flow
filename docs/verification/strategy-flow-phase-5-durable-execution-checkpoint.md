# Strategy Flow Phase 5 Durable Execution Checkpoint

**Status:** Phase 5 complete

**Date:** 2026-09-06

## Scope

This checkpoint closes the linked Phase 5 execution gaps:

1. Candidate consumption, order intent, symbol ownership, and account-scoped
   portfolio-risk reservation commit atomically.
2. Order, position, fill, and risk records are scoped to the configured broker
   account; startup and every broker mutation validate that binding.
3. A leased dispatcher recovers `INTENT` and `SUBMITTED` work, adopts only an exact
   broker contract by deterministic client order ID, and does not duplicate an
   ambiguous submission.
4. Broker cumulative partial fills become one idempotent account-scoped position
   delta, including concurrent REST/stream repair.
5. Cancellation is journaled as `CANCEL_PENDING` before broker DELETE and is retried
   after restart. A trailing-stop PATCH proves ownership first and succeeds only
   after the requested stop is visible in a fresh broker snapshot.
6. Regular and extended-hours exits preserve the requested order contract. An
   extended-hours exit additionally requires the explicit toggle, asset eligibility,
   a fresh executable quote, and a limit/DAY order.
7. A stable position-generation identity survives partial fills and partial exits,
   and changes only after the position is flat and reopened.
8. Protective-stop PATCH commands and successor broker identity are durable and
   recoverable after ambiguous responses or restart.
9. A position exit cannot reach broker POST until owned protection for the same
   position generation is terminal and broker/local quantity agrees. Competing
   protective dispatch fails closed while the exit owns that generation.
10. Recovery expires a protective-stop intent from a closed or replaced position
    generation before broker POST, and resumes a position exit that crashed after
    protection cancellation by preparing and dispatching the same durable intent.
11. Protective replacement verification and its successor lifecycle event commit in
    one SQLite transaction. Later replacement and cancellation commands resolve the
    latest verified successor instead of addressing a superseded broker order.
12. Startup readiness reports the initial dispatch-recovery result. Trading readiness
    remains degraded until that recovery cycle has completed successfully.
13. A deterministic execution kernel and immutable stateful book now model time-valid
    quote or explicit synthetic execution, shared slice participation, partial/no
    fills, costs, impact, persistent stop-limit activation, partial replacement,
    IOC remainder cancellation, sell-side-only regulatory fees, immutable replay
    contracts, partial exits, and conservative competing-exit ordering without
    allowing an exit to reverse the position. Conditional fills that overlap a
    mid-slice cancel or replacement are rejected until the caller supplies a smaller
    causally ordered slice.
14. One chronological portfolio coordinator drives that book across all symbols and
    strategies. Protective stop/target orders are installed after entry fill and can
    only trigger on later available market slices; mixed execution timeframe or
    slippage policies for the same symbol fail closed.
15. Swing partial fills remain protected while the entry window is open. At its
    deadline, cancellation uses the final broker cumulative quantity; any race fill
    is protected before a below-minimum `MIN_FILL_ABORT` exit is submitted.
16. Regular entries preserve their requested market/limit contract. They are simple
    orders followed by independent GTC protection; extended-hours entries remain
    blocked while eligible risk-reducing limit/DAY exits require the explicit toggle.
17. Graceful shutdown closes and drains the broker-mutation boundary, confirms flat
    state through terminal order plus local and repeated broker evidence, restores
    protection after an incomplete exit handoff, and verifies a FULL SQLite WAL
    checkpoint. An acknowledgement-journal failure blocks all new entries.
18. Generic position-exit handoff is exception-safe. Broker rejection, caller
    cancellation, and broker/local quantity mismatch restore a successor durable GTC
    stop with an independent bounded token; an accepted broker exit with a failed ACK
    journal is recognized as active and is never raced by a second sell order.
    A persisted broker attempt that is not visible yet is treated as ambiguous;
    the exit remains recoverable and no competing stop is created.
19. Exit expiry atomically requires an unattempted, unleased durable intent.
    Dispatch-attempt recording rechecks lifecycle state in the same immediate
    transaction, closing the cross-dispatcher expire-versus-submit race.
20. A previously ambiguous exit that later appears rejected, canceled, or expired
    at the broker is journaled, including partial-fill repair, before protection is
    restored for the refreshed remaining broker quantity.

## Safety Invariants

- A stopped or cancelled run cannot originate a fresh broker post.
- A definitive broker rejection terminalizes the order and releases unused entry
  risk; an ambiguous transport failure leaves the order pending for adoption.
- A broker order with mismatched symbol, side, quantity, type, time-in-force, order
  class, extended-hours flag, limit, stop, or bracket prices is never adopted.
- The same provider execution ID may exist in two accounts without suppressing either
  fill; duplicates remain idempotent within one account.
- A stale position generation cannot submit an exit for a newer position.
- One failed cancellation recovery does not prevent recovery of other owned orders.
- Stop replacement cannot target an unowned broker order, and caller state is not
  advanced when broker verification fails.
- Cancellation of a replaced stop resolves and verifies the successor client and
  broker IDs before broker DELETE.
- A broker/local position mismatch expires the unsubmitted exit instead of leaving a
  reservation that indefinitely blocks protection repair, and restores protection
  for the broker-confirmed quantity.
- Ordinary active-order queries exclude backstops, while the exit handoff uses a
  dedicated active-protection query; protective orders cannot disappear from the
  cancellation path because of entry duplicate filtering.
- TradingFlow ATR repricing operates only on ordinary `stop` and `stop_limit`
  orders; it never attempts to reprice a broker-native `trailing_stop`.

## Verification

```powershell
dotnet test TradingFlow.sln --no-restore
dotnet ef migrations has-pending-model-changes --project src\TradingFlow.Data\TradingFlow.Data.csproj --startup-project src\TradingFlow.Web\TradingFlow.Web.csproj --no-build
dotnet build src\TradingFlow.Mobile\TradingFlow.Mobile.csproj -f net10.0-android --no-restore
git diff --check
```

Results on 2026-09-06:

- Complete .NET suite: 1,491 passed, 0 failed, 0 skipped.
- Focused chronological execution, synchronization, reconciliation, shutdown,
  SQLite durability, broker adapter, and protection slice: 159 passed, 0 failed,
  0 skipped.
- Playwright desktop/mobile web suite: 70 passed, 0 failed.
- EF Core model drift check: no pending model changes.
- Android `net10.0-android` build: succeeded, 0 warnings, 0 errors.
- Diff whitespace validation: passed.

## Outside Phase 5

Phase 5 does not promote a strategy or supply paper-shadow evidence. Those lifecycle
and evidence gates remain separate requirements before live routing. Paper/live
short entry also remains fail-closed until its borrow and protection contract is
implemented and verified.
