# Paper execution smoke — prepared 27 September 2026

Status: waiting for an executable market session and authenticated administrator access. No broker writes have been made.

At 20:03:52 UTC Alpaca reported: market closed; next regular open 28 September 2026 at 15:30 Europe/Berlin; account ACTIVE, unblocked and matching the configured account; zero open orders and zero positions.

The existing administrator-only operator_override workflow is suitable for a bounded execution-infrastructure smoke. It does not promote a strategy or validate autonomous strategy admission. OrderWorkflowService requires real administrator authorization at preview and confirmation, an unexpired wishlist universe snapshot, exact research artifact identity, matching paper account, protected whole-share limit/day orders, fresh SIP quote and the normal shared risk/journal/protection path. No fake authentication or fabricated strategy authorization is permitted.

Proposed bounded run:

1. Recheck broker clock, account binding, open orders and positions. Use the normal durable TradingFlow journal and real administrator login with CSRF. Do not start unrelated paper jobs or automated strategies.
2. Use a dedicated persisted smoke-test wishlist containing AAPL; capture fresh membership and select an existing exact swing research identity. Do not mutate or promote the artifact.
3. Request one whole paper share only, maximum entry notional USD 500, regular session only. Derive the marketable limit and tick-rounded stop/target from fresh SIP prices at execution time; keep planned loss at or below USD 10 and all existing risk rules. A rejection is a test failure/blocked result, never permission to weaken policy.
4. Preview with executionPolicy operator_override and no candidate/run claim, then confirm through /api/v1/orders/confirm. Retain preview, immutable idempotency key, intent and broker identities. On an ambiguous response reconcile the same command; do not submit a new key.
5. Observe actual broker fill and matching durable order/position events. Verify broker-side protective stop coverage for the entire filled quantity; a local success flag alone is insufficient.
6. Close only the test's filled quantity through /api/v1/positions/close. Verify exit fill, protective-child terminal states, zero remaining test position, no orphan test orders, and reconciled audit quantities. Never cancel unrelated account orders or positions.
7. If entry does not fill within two minutes, cancel only this test's remaining entry through the normal workflow and reconcile possible partial fills. If protection fails after a fill, use the existing emergency/protection coordinator and verify closure. Keep the host running while exposure or reconciliation is unresolved; report any failure rather than marking the gate passed.
8. Save sanitized evidence, then stop only task-owned services when the test is flat and reconciled.

This run establishes order/fill/protection/exit plumbing only. Strategy-driven discovery/admission, promotion and multi-session calibration remain distinct evidence gates. No order is queued overnight. Do not place a live order or route directly around TradingFlow through raw broker POST calls.

Preflight verification: 59 tests passed, zero failed/skipped (6 seconds), using the existing built test assembly. Filter: OrderWorkflowIntegrityTests, AlpacaManualOrderServiceTests, OrderSubmissionServiceTests, AccountReconciliationServiceTests, ReconciliationPersistenceTests, ManualOrderTicketServiceTests, OperatorAccessTests. TRX: .test-tmp/external-acceptance/paper-execution-preflight.trx. No application code or strategy authorization changed.
