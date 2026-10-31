# Remove only the minimum reward/risk filter

Approved by the user on October 29, 2026: disable the 1.5 minimum reward/risk filter, rerun the same eight stocks for October 27–28, retain capital limits and costs, obtain independent quant review and compare outcomes.

## Scoped change

Register a separate immutable research recipe, `technical.zone-volume-spread-no-rr` v1.0.0, copied from the approved volume/spread recipe. Preserve the earlier recipe and results. Use zero in the existing `minimum_reward_risk` field to explicitly disable the filter; zero must not silently impose a nonnegative net-reward gate. Negative configuration thresholds remain invalid. Keep calculating the estimated ratio for audit, even when fees make estimated net reward negative.

Reuse the current chronological alternative selection and shared risk planner. Select the first eligible, successfully sized alternative in the frozen order without requiring its ratio to meet a minimum. All positive-price/ATR/directional-stop geometry, capital sizing, entry-window, same-day close and execution-accounting checks remain. Report a distinct selected reason and display that the minimum reward/risk filter is disabled in the existing API/shared client presentation.

No new execution path, custom run program, per-run configuration, provider or optimization is needed. This only changes one selectable strategy setting's disabled behavior and adds its immutable research recipe.

## Unchanged experiment

- CRDO, MU, MXL, NVDA, PATH, POET, SMCI and TSM; one stock per portfolio, $10,000 each.
- October 27–28, 2026 New York regular sessions; use the same prepared source versions where available.
- Existing causal RTH 15-minute zones and five-minute confirmations; both-side RVOL >= 1.2; no VWAP gate; next-zone target and zone/ATR stop.
- One position, at most two filled entries per day, 1% account risk budget, 25% position-notional cap.
- Estimated full spread 10 basis points, per-side slippage 10 basis points, existing impact/participation and fixed/regulatory fees.
- Last entry 15:50 New York inclusive; liquidation 15:55, relative to the session calendar.
- Normal saved-plan workflow; monthly results separately per strategy and stock. No pooled return.
- Preserve all core data, prior results, new outcomes and intermediate evidence. Trading stays paused. Access remains local only, as explicitly requested by the user; do not start ngrok.

## Checklist

- [x] User approves this exact removal and same-stock/date comparison.
- [x] Implement configurable disabled behavior, separate recipe and clear existing-client presentation.
- [x] Focused tests prove first-alternative selection, ratios below zero and 1.5 when disabled, old 1.5 behavior unchanged, invalid negative setting rejection and normal registration/integration.
- [x] Independent engineering and quant review of the delta.
- [x] Run all eight portfolios through the normal saved plan.
- [x] Independently reconcile trades, costs, cash and monthly outcomes; compare with both prior variants.

This experiment tests the effect of removing a filter; it does not authorize live trading or claim that a larger trade count will improve returns.
