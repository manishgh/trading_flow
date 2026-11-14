# Monthly ETF paper rotation: revised design review

Updated: 14 November 2026. Supersedes the same-day proposal that assumed existing paper submission could place a same-open swap.
Status: **deferred by the user for later research; no monthly driver or trading grant added.** The protection and execution requirements below remain future design prerequisites.
Tracking: [audit repair plan](../../implementation/audit-repairs-2026-11-14.md).

## Intended outcome and evidence boundary

Prove that the running system computes the frozen rotation's monthly decisions and records explainable paper execution. This does not establish profitable trading or authorize real-money execution. Preserve the strategy identity, its untouched holdout and the user's success thresholds.

The old proposal described opening sales followed by opening purchases, reuse of `MeasureAdmissionCapacity`, and a paper experiment grant as sufficient. Independent code/design review found that these are insufficient:

1. Unfilled sales do not replenish broker buying power. Submitting a sell first does not mean it fills before a buy. The backtest admission function intentionally releases planned sale proceeds at prior-close values; it cannot determine spendable paper cash.
2. Current Alpaca entry and exit adapters reject opening-auction time-in-force. Auction support needs explicit order contracts, cutoff handling and tests.
3. Rotation declares no initial stop and no target. The common entry gate requires positive stop and target distances, recovery checks protection, and the operating boundaries require protective-stop coverage. A paper-experiment grant does not override these rules.
4. Broker-account buying power is not the same as this run's unreserved cash. Other runs, manual positions and working orders must be accounted for.

## Policy decision before implementation

Keep monthly execution disabled while the user chooses between preserving the stopless rotation with an explicitly reviewed paper-only protection-policy exception, or researching a separate protected strategy with its own identity and frozen test. Neither choice is implied by the repair authorization. Never add fake stop/target values or weaken the shared gate to make the existing identity pass.

The reporting and paper-lifecycle repairs do not depend on that decision and proceed separately.

## Shared decision calculation

Reuse `DecisionSchedules`, `ExchangeSessionCalendar`, `TechnicalFactorCalculator` and the existing 12-1 ranking calculation, extracting the CLI ranking into a shared engine function only when this implementation is approved.

For the target execution session, use the eligible membership computed strictly from earlier bars, ending at the completed month-end decision close. Rank that eligible set with the existing deterministic tie ordering, select the top three, then apply the 210-session trend filter. A top-three fund failing the filter leaves its slot in cash; do not substitute rank four. Preserve later listings, split exclusions and the distinction between a valid empty membership list and unavailable membership evidence.

Record account, owning run, exact strategy artifact hash, decision and target execution sessions in New York, frozen bar/membership fingerprints, rankings, trend values, target and every exit condition. The unique executable decision key is (account, owning run, New York decision session). Store the strategy artifact hash, target execution session and input fingerprints as immutable contents, not new identity dimensions. Preserve subsequent provider corrections as evidence revisions; a correction cannot create another executable decision after reservation or submission.

## Funding and order timing approaches

### A. Reconciled sales, then bounded purchases (recommended engineering approach)

Use a fixed execution window in the next target regular session. Send owned-position sales through the existing intent and dispatch journal. Observe and reconcile actual fills before reserving new purchases. A partially sold holding retains its slot; rejected or ambiguous sales release neither a slot nor cash. New purchases use explicit price limits, whole-share quantities, fees and existing unreserved run cash, bounded by the configured capital, 32.5% position cap and 2.5% reserve. Apply fresh broker/account eligibility as an additional constraint, never as permission to spend other runs' capital.

This approach changes execution timing from the backtest's simultaneous opening fills. It requires a separate matching execution replay before activation. Target/ranking parity is still required, but identical holdings or opening fills must not be claimed. Define the exact time window and price-limit policy before implementing; no overnight fallback for missed orders.

### B. Opening auction with independently funded buys

If opening-auction timing is required, reserve buys solely from pre-existing unreserved cash and already free slots. Do not count unfilled opening sales. Add explicit supported auction contracts and price bounds; verify the configured paper account's actual behavior first. The result may leave cash or an incomplete rebalance when there is insufficient prefunded capacity. This divergence must be modeled and visible.

Alpaca documents that OPG orders submitted between 09:28 and 19:00 Eastern are rejected, after-19:00 orders queue for the next open, and unfilled auction remainders cancel. An after-close scheduler cannot blindly submit an OPG order at 16:00. Use the target exchange session and official broker rules, not a fixed UTC offset or submission-date assumption.

Sources inspected during independent review: [Alpaca orders](https://docs.alpaca.markets/us/docs/orders-at-alpaca), [paper simulation](https://docs.alpaca.markets/us/docs/paper-trading). Paper simulation does not by itself prove live opening-auction execution quality.

## Durable execution and recovery

Reuse the existing order-intent journal, fenced dispatcher lease, pre-POST persistence, broker lookup, position ownership, reconciliation and protection services. Add no parallel broker submission path.

- Derive stable per-order client IDs from account, owning run, frozen monthly decision and order leg. Persist the exact payload before submission.
- A timed-out POST may have succeeded. An initial lookup miss is not proof that it did not; retain the ambiguous state for broker confirmation or operator resolution rather than submitting a second order.
- Reconcile fills idempotently, including duplicate and out-of-order notifications. Update owned quantities and reservations atomically.
- Enforce strategy authority, run entry health, target session, deadline, fresh market inputs, available run cash and reservations immediately before the first broker POST, including recovery/background dispatch.
- After the execution deadline, forbid the first POST of every planned monthly buy and sell, including recovered/prepared monthly exits. Cancel working remainders and reconcile their terminal filled/canceled state before releasing reservations or slots; unresolved broker states retain reservations and are reported as incomplete. Continue reconciliation, adoption, cancellation, required protection and separately authorized safety exits. Never roll a missed monthly order into another session without a new recorded decision.
- Pause/cancel stops new entries and cancels pending eligible entry work through existing machinery. It does not automatically sell holdings. Unknown broker state remains visible.
- If local position ownership or account cash cannot be reconciled, new entries stay disabled. Do not silently net a manual/other-run holding into this run.

Sources: [Alpaca order lifecycle and timeout guidance](https://docs.alpaca.markets/us/docs/working-with-orders), [lookup by client order ID](https://docs.alpaca.markets/us/reference/getorderbyclientorderid).

## Acceptance tests before activation

1. **Decisions:** every development-period month-end (2017-2023) matches the frozen eligible universe, rankings, target and exit reasons. No 2024-2026 holdout access. Missing/corrected/delayed input is explicitly withheld or versioned, never guessed.
2. **Cash and capacity:** fully invested swap; zero, partial, rejected and ambiguous sales; opposing gaps and fees; competing buy reservations; other-run/manual same-symbol exposure. No spending of unfilled proceeds, unauthorized borrowing or duplicate exits. A partial sale retains capacity usage.
3. **Recovery:** crash before POST, accepted POST with lost response, lookup lag, failed acknowledgement persistence, two workers, duplicate/out-of-order fills, revocation and stopped run. The same persisted command is adopted or reconciled once; ambiguity is never treated as safe to resend.
4. **Time:** DST, holidays, early closes, restart on both sides of the cutoff, missed window and after-19:00 auction targeting. Assert exact New York decision and execution sessions and documented late-order behavior.
5. **Execution comparison:** replay the chosen timing protocol and show target parity separately from fill/holding differences, partial fills, rejects and misses. Store modeled reference, send-time executable quote, broker fill and reason for each divergence.
6. **Protection:** resolve the policy conflict explicitly and verify the resulting authorized strategy identity through entry, recovery, dispatch, cancellation and shutdown. No fake protection values.
7. **Reviews:** independent plan/execution reviewer plus a different independent code/design reviewer; focused compiler, integration, persistence, safety and deterministic replay tests. Provider paper smoke is a separate explicitly activated checkpoint, never an incidental test.

## Review record

Independent Reviewer B (`design_review`) rejected the original proposal and identified funding, broker TIF, stopless protection, ownership, cutoff and recovery issues. Independent Reviewer A (`plan_review`) confirmed the need for separate decision parity and execution-timing verification. This revision records those findings and the smallest reusable approaches. Final design acceptance is pending the protection-policy and execution-protocol decisions; implementation has not begun.

Follow-up independent design review accepted this revision with two clarifications: executable uniqueness excludes mutable data fingerprints, and first-submission deadlines cover both monthly order sides. Both clarifications are incorporated above.

## Subsequent user clarification

The user distinguishes monthly rotation for holdings lasting months from intraday and swing trades. Swing exits may follow a defined support break; stops need not be an arbitrary fixed percentage. Preserve that distinction in the next design revision. The current monthly artifact stays research-only because its execution/protection policy has not been implemented or reviewed as a separate authorized long-holding path. Current shared gates remain intact during the reporting/lifecycle repairs.

Also see the tracking plan's research-source clarification: this exact 12-1 / 210-session / no-trimming combination is an adaptation, not a verified exact reproduction of one published recipe.
The user subsequently instructed that sector rotation be kept separate and disabled for current use. A reversible catalog disable feature is now included in checkpoint S1 of the tracking plan. This is not retirement or deletion; monthly implementation and activation are deferred until a future explicit request.
