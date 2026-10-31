# Strategy 1 Intraday: concrete rule review

29 October 2026. The user approved the remaining definitions and selected VWAP plus volume only. Implementation is underway; the CRDO run follows focused checks and independent review. Core data and earlier results remain preserved.

## Confirmed by the user

- Display name: **Strategy 1 Intraday**. This is a new immutable strategy, preserving ORB and its results.
- **15-minute zones and five-minute entries**.
- Both **breakout/retest** and **rejection/bounce**, separately labelled within one strategy and one portfolio.
- Retain the existing candle stop, actual-fill 3R target, completed-close continuation protection, two filled trades per day, one open position and same-day closing rule.
- Run CRDO for **October 27–28, 2026**, with **$10,000**. The run is already requested; it does not need another permission after the rules are settled and implementation verified.
- Reuse shared components, preserve all core data and results, and verify independently.

## Exact supplied zone mathematics

At completed 15-minute bar t, test i=t-3 against the three closes on each side. Peak means Close[i] >= every neighboring close; valley means Close[i] <= every neighbor. A pivot is available only when the third right-hand bar closes: **45 minutes after the pivot candle closes**. Never draw it into an earlier decision.

Peak rectangle: [max(Open[i], Close[i]), High[i]]. Valley rectangle: [Low[i], min(Open[i], Close[i])]. Keep exact raw prices. Equal-close windows can satisfy both definitions; zero-width wicks remain zero-width. Neither may be silently discarded.

Merge eligibility is the user's literal formula abs(newBottom-zoneBottom) <= 0.35*ATR14. This is bottom-distance, not the gap between rectangles: it can merge disjoint rectangles or fail to merge overlapping ones. Do not substitute an overlap algorithm.

The supplied downward flip is strict: SUPPORT becomes RESISTANCE when a completed close < bottom-0.2*ATR14. Equality does not flip.

## Approved remaining definitions

The user approved this rule bundle and the entry filter below.

| Missing detail | Concrete proposal |
|---|---|
| Session/history | Regular market hours only. Carry zones from the previous five completed trading sessions plus the current session; use the exchange calendar. The rolling completed-bar sequence may cross overnight boundaries; it contains no premarket bars. |
| Indicators | Use the existing indicator warm-up. ATR14 for zone operations comes from the latest completed 15-minute bar; entry filters use completed five-minute evidence. No zone update until ATR is available and positive. |
| Merge behavior | Peak starts as RESISTANCE; valley as SUPPORT. Merge only equal current roles. If several zones qualify, select the oldest; expand to min(bottoms)/max(tops). Start count at one and increment once per merged confirmed pivot, not per price visit. Process peaks then valleys, then flips. Expire based on the most recent merged pivot's session. |
| Reverse flip | Add the symmetric transition RESISTANCE to SUPPORT when a completed 15-minute close > top+0.2*ATR14. This upward flip was not in the pasted specification. |
| Breakout/retest long | A completed 15-minute close flips resistance that was already known before that 15-minute candle began. Freeze that box. A later completed five-minute candle must overlap it and close above its top. Enter after confirmation. Short is the mirror image. Newly created zones may record a same-close flip, but cannot arm a retrospective breakout from that creation candle. |
| Bounce long | A completed five-minute candle overlaps support already known before that candle and closes above its top. Short: overlap resistance and close below its bottom. A fresh confirmation is required; merely remaining outside the box is not another signal. |
| Pattern collision | Retest takes priority over bounce. If several zones qualify for the same pattern, use the smallest distance from the completed close to the nearest box edge, then oldest as a tie-breaker. Submit only one parent order. Both patterns share the two-trade daily limit. |
| Stop/order | Stop at the five-minute confirmation candle's low for longs/high for shorts, with existing outward tick rounding. Route a market intent only after that candle closes; size from already-known evidence. Preserve actual gap fills and shared risk-reduction behavior. |
| Expiry/rearm | Armed retest expires at the daily deadline or a completed five-minute close strictly below the frozen bottom for a long / strictly above its top for a short; equality does not cancel. Consume the armed retest when its first qualified order intent is emitted, even if later admission rejects it; another retest entry requires a new flip. No pending entry overnight. After resolution, bounce requires fresh touch/rejection; do not inherit ORB's opening-range rearm rule. |
| Simultaneous clocks | A zone confirmed/flipped at 10:30 is usable during the 10:30–10:35 entry candle, not retrospectively for a 10:25–10:30 touch. Existing armed setups use their frozen zone version. |

### Approved entry filter

Long confirmation closes above regular-session VWAP; shorts close below it. Confirmation-bar volume must be strictly greater than the median volume of that same five-minute interval over the preceding twenty trading sessions. The user selected VWAP plus volume only: no EMA entry filter.

This volume test occurs at confirmation. It does not reuse ORB's fixed first-15-minute rejection for the entire day. RTH-only VWAP needs RTH inputs: the existing VWAP routine resets by New York date but includes all bars it receives.

### Quote checks and historical execution

The requested bid/ask layer must use actual historical quotes, not current quotes or a manufactured spread from candles. Reuse the common quote freshness/spread controls and freeze their effective values in run evidence. Registry defaults are a maximum quote age of **2 seconds** and maximum spread of **20 basis points (0.20%)**; inspect the effective settings before running rather than assume the defaults are active.

Reject missing, stale, crossed or conflicting quotes through existing quote eligibility rules. Use the existing chronological SIP trade/quote matcher and real bid/ask spread, displayed-size/participation rules and declared fees. Do not claim an ask/bid alone proves every fill. No silent candle-only fallback. The exact shared execution contract must cover both directions, protective orders, completed-close management and the same-day deadline. Historical short borrow and original receipt/revision availability remain explicit research limitations.

## What exists and what must be built

| Reuse | Missing integration |
|---|---|
| Shared candle reader, provider archives, resampling, exchange calendar, ATR/EMA/VWAP/RVOL | A small stateful close-pivot rectangle calculator with immutable confirmation and zone-transition evidence. Existing VCP pivots use highs/lows, stricter ties and alternating pivots, so they are not a substitute. |
| Shared strategy decision kernel, risk planner, candidate journal and strategy catalog | Zone-pattern evaluator, normal recipe parsing and chronological 15m/5m integration. |
| Shared SIP quote/trade matching, source manifests, costs and portfolio accounting | Current event portfolio controller accepts daily long-only behavior. Extend that shared controller with a distinct intraday contract for long/short, two entries, candle-driven protection and deadline exits. Current ORB executor is OHLC-only and cannot validate historical spread. |
| Existing saved plans, monthly reporting and API/Web/Android presentation | Expose new identity, pattern, pivot/confirmation clocks, zone bounds/role, filter evidence and quote/order reasons through existing result flows. No new screen or separate application. |

Keep zone calculation, entry-pattern state and execution separate. Small functions should confirm one pivot, form a box, select/merge a zone, flip/expire zones, and emit evidence. Process each completed bar once rather than repeatedly rebuilding historical zones. Keep independent state per symbol/timeframe. Never mutate a previously published zone snapshot. No custom CRDO runner or run-specific executable/configuration.

Main integration locations: Domain strategy/evidence contracts; Engine indicators/strategies/decision kernel and YAML parser; Backtesting chronological candidate generation, event-source binding and event portfolio controller; Web run-config/session/execution-policy resolvers; shared Contracts presentation and normal catalog.

## Work and verification checklist

- [x] Inspect repository instructions, existing indicators, pivots, runner and quote execution.
- [x] Independent Sol 6.1 High design review identifies differences and reuse.
- [x] Record confirmed timeframe, patterns, risk management and requested CRDO dates.
- [x] Settle the filter answer and remaining zone/pattern definitions above.
- [x] Implement shared zone calculation and pattern evaluation with commented, small functions.
- [x] Integrate normal configuration/catalog/API/Web/shared Android evidence.
- [x] Extend and independently review the shared historical quote execution contract.
- [x] Verify causality, exact ties, merge/flip boundaries, carry/expiry, clock alignment, session VWAP, gaps and warm-up.
- [x] Verify long/short quotes, stale/missing quotes, partial/gap fills, original 3R, protection, duplicate signals, daily cap and closing deadline; preserve ORB regression tests.
- [x] Use the existing saved-plan worker for the requested CRDO October 27–28 run, obtaining missing records through existing provider components.
- [x] Independent quant review; publish per-pattern trades and monthly strategy/portfolio results with zone charts. Keep all source data, failures and results; no cleanup without the existing exact-run reviewer approval.

Authorization covers research implementation and the requested backtest, not paper/live activation. Completion is recorded only after the corresponding checks and evidence exist.

Completed evidence: outputs/zone-strategy-20261029/results.md and independent-quant-actual-run-review.md. Final ordinary saved-plan job: 72a02598-c5da-f755-670e-a2f28d95a275. Two sessions, one rejected setup, zero fills; quote age 7.534 seconds exceeded the frozen 2-second limit. Web HTML/API verified; visual browser automation could not initialize and no Android device/APK check was performed.
