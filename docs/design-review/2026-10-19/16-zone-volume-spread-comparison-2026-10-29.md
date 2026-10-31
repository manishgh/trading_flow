# Supplied Python volume/spread comparison

Status: approved implementation and eight-stock research comparison completed on October 29, 2026. User requested testing `gemini-code-1793307629882.py`, selected a candle diagnostic and approved a fixed 0.10% full spread with existing per-side slippage and fees. The user approved existing regular-session zones, last entry at 15:50 New York and liquidation at 15:55.

## Intended comparison

Use the same eight separate $10,000 portfolios (CRDO, MU, MXL, NVDA, PATH, POET, SMCI, TSM), October 27–28, 2026. Preserve both earlier strategy results. Reuse normal saved-plan execution, shared risk sizing, execution, artifacts and monthly reporting. No custom runner, per-run source code or temporary strategy configuration.

The supplied file requires RVOL >= 1.2 on both sides, removes VWAP and overextension filters, retains next-zone targets and zone-plus-0.15-ATR stops, rejects spreads above 20 basis points, and accepts estimated reward/risk >= 1.5 after two $1 ticket fees. This is not the previously discussed costs-plus-2R target.

Keep the literal per-zone ordering: sort eligible zones by bottom; check long then short for each zone; return the first alternative passing every rule. Preserve the one-sided previous-low touch test and the short pattern's lack of a role requirement. Target filtering uses the estimated ask for longs and bid for shorts. Use existing confirmed pivot count >= 2 as the documented substitute for the omitted Python touches implementation.

## Costs, quantity and chronology

- At confirmation, construct estimated bid/ask around the completed five-minute close using half the approved full spread: bid = close * (1 - 0.0005), ask = close * (1 + 0.0005). These are synthetic prices, not historical quotes.
- The fixed 10-basis-point estimate always passes the 20-basis-point spread gate. This experiment cannot establish whether actual spreads would have passed.
- Freeze the ordered price/volume-qualified zone alternatives and their evidence at confirmation. At chronological portfolio admission, use the existing shared risk planner and current known cash/equity to size each alternative. Reserve only the first alternative whose capped quantity passes the supplied fee-adjusted ratio. Do not reject the whole signal merely because its first geometric alternative fails the ratio.
- Use actual planned quantity under the existing 1% risk budget and 25% position-notional cap, not the snippet's 100-share default. One open position and at most two filled entries per stock/session remain.
- Label the supplied ratio as estimated reward/risk after ticket fees. Its formula includes the entry-side quote and flat fees, but omits future exit spread, slippage and regulatory fees. Preserve that literal gate rather than silently imposing an additional gate.
- The simulator separately applies the approved full spread through its existing synthetic-spread field, existing 10-basis-point per-side slippage, impact if configured, and fixed/regulatory fees. Do not apply half-spread to an already adjusted price again; the quote estimate used for evaluation is not an extra accounting charge.
- Signals use completed bars, entries use subsequent openings, and source/target zones must be known before confirmation. Require a preceding bar from the same session and valid positive ATR and directional stop/entry/target geometry. Round levels with the shared tick policy before checking the ratio.

## Session decision

The file contains no constructor or zone builder and comments that zones use all candles. The user approved reuse of existing regular-session 15-minute zones. Implementing extended-hours zones would need a corresponding specific data/session design; it must not happen implicitly from a comment.

Use the exchange calendar rather than a fixed Berlin clock. For October 27–28, 09:30–16:00 New York corresponds to 14:30–21:00 Berlin. Other DST combinations and early closes differ.

The file permits entries until the market close but supplies no liquidation logic. Approved reconciliation with the retained same-day rules: last entry at 15:50 New York inclusive (close minus ten minutes), liquidation at 15:55 (close minus five), both relative to actual session close. Do not admit an entry at or after liquidation. The user explicitly approved these cutoffs before implementation.

## Smallest component changes

1. Separate immutable research recipe and typed parameters/evidence for this comparison; preserve earlier identities.
2. Reuse existing zone construction and RVOL, adding the supplied pattern evaluation with ordered alternatives.
3. Reuse `SharedOrderRiskPlanner` at portfolio admission for quantity-dependent selection and fee-adjusted ratio. Keep strategy evaluation, sizing and fill accounting in separate small functions.
4. Reuse the current fixed-zone candle executor and its simulator's synthetic-spread support; extend evidence/API/Web/shared client presentation to expose spread assumption, ratio and selection/rejection reasons.
5. Use normal saved plans and existing prepared data. Preserve all core data, prior results and new evidence.

## Verification checklist

- [x] Read the supplied file and inspect shared components.
- [x] Independent quant and senior-engineering read-only design assessment.
- [x] User chooses candle diagnostic and 0.10% estimated full spread.
- [x] Resolve zone-input sessions and confirm the proposed final-entry cutoff.
- [x] Implement the scoped reusable comparison with focused tests.
- [x] Verify ordering when earlier alternatives fail reward/risk, quantity/fees, spread accounting once, future-bar rejection, invalid stops, cutoff and same-day exits; retain prior-policy regressions.
- [x] Independent engineering and quant review of implementation.
- [x] Run the eight portfolios using the normal saved-plan workflow and independently reconcile monthly/trade results.

Implementation and the eight-stock research comparison are approved. No core-data deletion or trading is authorized.

Completed evidence: outputs/zone-strategy-20261029/volume-spread-eight-stocks/results.md, verification.md and quant-result-review.md. Normal plan 48; all eight completed, nine source queries reused, zero downloaded. No core data or prior results deleted; trading remains paused.
